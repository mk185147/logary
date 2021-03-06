namespace Logary

open System.Diagnostics
open System.Net
open Logary
open Logary.Model
open Logary.Trace
open Logary.Metric

/// The type-signature for middleware; next:(Message -> Message) -> message:Message -> Message.
type Middleware =
  (Model.LogaryMessageBase -> Model.LogaryMessageBase) -> Model.LogaryMessageBase -> Model.LogaryMessageBase

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Middleware =

  /// This is the identity middleware.
  [<CompiledName "Identity">]
  let identity: Middleware =
    fun next msg -> next msg

  /// Sets the host name as a context value
  [<CompiledName "SetResource">]
  let setResource (r: Resource): Middleware =
    fun next msg ->
      msg.setContextValues(r.asMap())
      next msg

  /// Sets the host name as a context value
  [<CompiledName "SetDNSHost">]
  let setDNSHost: Middleware =
    let host = Dns.GetHostName()
    fun next msg ->
      msg.setContext("host", Value.Str host)
      next msg

  /// Sets the service name as a context value
  [<CompiledName "SetService">]
  let setService (name: string): Middleware =
    fun next msg ->
      msg.setContext("service", Value.Str name)
      next msg

  let private pn = Process.GetCurrentProcess()

  /// Sets the current process' name as a context value
  [<CompiledName "SetProcessName">]
  let setProcessName: Middleware =
    fun next msg ->
      msg.setContext("processName", Value.Str pn.ProcessName)
      next msg

  /// Always sets this context value.
  [<CompiledName "SetContext">]
  let setContext name value: Middleware =
    fun next msg ->
      msg.setContext(name, value)
      next msg

  /// WARNING: carefully consider if you want to do this; it is only supported by the Task-infrastructure, not by any
  /// TheadPool based infrastructure.
  [<CompiledName "AmbientSpanId">]
  let ambientSpanId: Middleware =
    fun next msg ->
      match ActiveSpan.getSpan () with
      | Some ambientSpanId ->
        msg.setContext("spanId", Value.Str (ambientSpanId.toBase64String()))
        next msg
      | None ->
        next msg

  /// Compose the list of middlewares together into a single Message->Message function.
  [<CompiledName "Compose">]
  let compose: #seq<Middleware> -> Model.LogaryMessageBase -> Model.LogaryMessageBase = function
    | xs when Seq.isEmpty xs ->
      id
    | middlewares ->
      Seq.foldBack (fun f composed -> f composed) middlewares id

  /// You can enable this middleware if you want Gauges and other Metrics coming as messages in Logary (e.g. via
  /// the Trace functionality), to be emitted into the `MetricRegistry`.
  ///
  /// Will send Logary's Gauges/SpanData or a plain event message with a metric counter to the (Prometheus) Metric
  /// registry.
  ///
  /// Will try to match Spans with conventions and map those to the Prometheus conventions:
  /// https://github.com/open-telemetry/opentelemetry-specification/blob/master/specification/data-semantic-conventions.md
  /// to:
  /// https://github.com/prometheus/client_ruby#histogram
  let internal metricsToRegistry (registry: MetricRegistry): Middleware =
    /// Find label name's value from Message's context, fields goes first
    let getLabelValues (message: Model.LogaryMessageBase) labelName =
      message.tryGetFieldString labelName
        |> Option.defaultWith (fun () ->
          eprintfn "Missing label \"%s\" specified in the LogaryMessageBase.counterConf field. Ensure you set all labels for this counter." labelName
          sprintf "Missing label \"%s\"" labelName)

    let eventAsCounter message conf =
      let counterMetric =
        GaugeConf.create conf
          |> registry.getOrCreate

      let counter =
        match conf.labelNames with
        | [||] ->
          counterMetric.unlabelled
        | labelNames ->
          labelNames
            |> Array.map (getLabelValues message)
            |> counterMetric.withLabelValues

      counter.inc 1.

    let gaugeToGauge sensorName (gaugeName, gauge: Logary.Gauge) =
      // TO CONSIDER: maybe make sensor + gauge as one metric name, not as one metric with gauge name as label
      let gaugeMetric =
        GaugeConf.create(sensorName,
                         "Gauge to Gauge via " + sensorName, gauge.unit,
                         [| "gauge_name" |])
          |> registry.getOrCreate
          |> Metric.labels [| gaugeName |]

      gaugeMetric.set gauge.value.asFloat

    let spanToGauge (span: Logary.SpanMessage) =
      let resource = span.resource |> Option.map (sprintf " from resource '%O'.") |> Option.defaultValue "."
      let gauge =
        GaugeConf.create(span.label,
                         "Span to Gauge via Span->Metric middleware (most recent span)" + resource,
                         U.Seconds)
        |> registry.getOrCreate
      gauge.unlabelled.set span.elapsed.TotalSeconds

    /// https://github.com/open-telemetry/opentelemetry-specification/blob/master/specification/data-semantic-conventions.md
    let spanWithConventions (span: Logary.SpanMessage) =
      let httpLabels () =
         List.concat [
           span.tryGetInt "http.status_code" |> Option.map (fun code -> ("code", string code) :: []) |> Option.defaultValue []
           span.tryGetString "http.method" |> Option.map (fun m -> ("method", m) :: []) |> Option.defaultValue []
           span.tryGetString "http.route" |> Option.map (fun r -> ("route", r) :: []) |> Option.defaultValue []
           span.tryGetBool "error" |> Option.map (fun error -> ("error", string error) :: []) |> Option.defaultValue []
         ]
         |> Map

      let histO, labels =
        match span.kind, span.``component`` with
        | SpanKind.Server, Some "http" ->
          registry.getOrCreate Conventions.http_server_request_duration_seconds |> Some,
          httpLabels ()
        | SpanKind.Client, Some "http" ->
          registry.getOrCreate Conventions.http_client_request_duration_seconds |> Some,
          httpLabels ()
        | _ ->
          None,
          Map.empty

      histO |> Option.iter (fun histogram ->
      histogram.withLabels(labels).observe span.elapsed.TotalSeconds)

    /// https://prometheus.io/docs/practices/naming/
    /// ...should have a (single-word) application prefix relevant to the domain the metric belongs to. The prefix is
    /// sometimes referred to as namespace by client libraries. For metrics specific to an application, the prefix is
    /// usually the application name itself. Sometimes, however, metrics are more generic, like standardized metrics
    /// exported by client libraries.
    ///
    /// Examples:
    /// - `prometheus_notifications_total` (specific to the Prometheus server)
    /// - `process_cpu_seconds_total` (exported by many client libraries)
    /// - `http_server_request_duration_seconds` (for all HTTP requests)
    ///
    /// Also tries to extract convention-based global gauges, see
    /// https://github.com/open-telemetry/opentelemetry-specification/blob/master/specification/data-semantic-conventions.md
    let processSpan (span: Logary.SpanMessage) =
      spanToGauge span
      spanWithConventions span

    fun next message ->
      message.counterConf
        |> Option.iter (eventAsCounter message)

      let sensorName = message.name.ToString()
      message.gauges
        |> Seq.iter (fun (KeyValue (k, v)) -> gaugeToGauge sensorName (k, v))

      message.tryGetAs<Logary.SpanMessage>()
        |> Option.iter processSpan

      next message