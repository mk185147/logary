﻿/// The FactoryApi is to make it much easier to configure Logary from a language
/// such as C#. It's AutoOpen because opening Logary.Configuration should
/// expose the ConfBuilder type inside this module without any further ado.
/// Besides that, this module doesn't contain much in terms of functionality/functions
/// but lets you configure all of that through interaction with the types/classes/objects.
namespace Logary.Configuration

open System
open System.Reflection
open System.Threading.Tasks
open System.Runtime.CompilerServices
open Hopac
open Hopac.Infixes
open NodaTime
open Logary
open Logary.CSharp
open Logary.Metric
open Logary.Internals
open Logary.Configuration
open Logary.Target
open Logary.Targets
open Logary.Target.FactoryApi
open System.Text.RegularExpressions


type internal ConfBuilderT<'T when 'T :> SpecificTargetConf> =
  { parent            : ConfBuilder        // logary that is being configured
    tr                : Rule               // for this specific target
    tcSpecific        : 'T option
    useForInternalLog : bool               // make internallog target config flexible
  }
with
  member internal x.SetTcSpecific tcs =
    { x with tcSpecific = Some tcs }

  interface TargetConfBuild<'T> with

    member x.MinLevel logLevel =
      { x with tr = { x.tr with Rule.level = logLevel } }
      :> TargetConfBuild<'T>

    member x.SourceMatching regex =
      { x with tr = { x.tr with Rule.hiera = regex } }
      :> TargetConfBuild<'T>

    member x.UseForInternalLog () =
      { x with useForInternalLog = true }
      :> TargetConfBuild<'T>

    member x.AcceptIf acceptor =
      { x with tr = { x.tr with Rule.messageFilter = acceptor.Invoke } }
      :> TargetConfBuild<'T>

    member x.Target = x.tcSpecific.Value

and internal MetricsConfBuilder(conf) =
  member x.conf = conf

  interface MetricsConfBuild with
    member x.AddMetric (pollEvery : Duration, name: string, metricFactory : Func<PointName, Job<Metric>>) =
      conf
      |> Config.withMetric (MetricConf.create pollEvery name (Funcs.ToFSharpFunc metricFactory))
      |> MetricsConfBuilder
      :> MetricsConfBuild

/// The "main" fluent-config-api type with extension method for configuring
/// Logary rules as well as configuring specific targets.
and ConfBuilder(conf, internalLoggingLevel) =

  let specificHieraForSetInternalLog = "^specificHieraForSetInternalLog$"

  let confBuilder conf =
    ConfBuilder (conf,internalLoggingLevel)
  
  let configInternalTargets conf =
    // find all targets set for internal log through a specific rule,
    // and remove these (specific rule/target) from logary conf, so normal logging is not affected. 
    // if there isn't any, use console as default

    let (normalRules, forInternalRules) =
      conf.rules 
      |> List.partition (fun r -> 
            match r with
            | r when r.hiera.ToString() = specificHieraForSetInternalLog -> false
            | _ -> true)
            
    let (normalTargets, internalTargets) = 
      conf.targets
      |> Map.partition (fun k v -> 
          forInternalRules 
          |> List.exists (fun r -> r.target = k.ToString())
          |> not)

    let internalTargets = 
      internalTargets 
      |> Map.toList
      |> List.map (fun (_,tci) -> fst tci) 
      |> function 
        | [] -> [Console.create Console.empty "internalConsole"]
        | tcs -> tcs

    { conf with rules = normalRules ; targets = normalTargets }
    |> Config.withInternalTargets internalLoggingLevel internalTargets


  member internal x.BuildLogary () =
    conf |> configInternalTargets |> Config.validate |> runLogary >>- asLogManager

 
  member x.InternalLoggingLevel(level : LogLevel) : ConfBuilder =
    ConfBuilder (conf,level)


  /// Call this method to add middleware to Logary. Middleware is useful for interrogating
  /// the context that logging is performed in. It can for example ensure all messages
  /// have a context field 'service' that specifies what service the code is running in.
  ///
  /// Please see Logary.Middleware for common middleware to use.
  member x.UseFunc(middleware : Func<Func<Message, Message>, Func<Message, Message>>) : ConfBuilder =
    conf
    |> Config.withMiddleware (fun next msg -> middleware.Invoke(new Func<_,_>(next)).Invoke msg)
    |> confBuilder
    
  /// Call this method to add middleware to Logary. Middleware is useful for interrogating
  /// the context that logging is performed in. It can for example ensure all messages
  /// have a context field 'service' that specifies what service the code is running in.
  ///
  /// Please see Logary.Middleware for common middleware to use.
  member x.Use(middleware : Middleware.Mid) : ConfBuilder =
    conf
    |> Config.withMiddleware middleware
    |> confBuilder

  /// Depending on what the compiler decides; we may be passed a MethodGroup that
  /// can be converted to this signature:
  member x.Use(middleware : Func<Message -> Message, Message, Message>) =
    conf
    |> Config.withMiddleware (fun next msg -> middleware.Invoke(next, msg))
    |> confBuilder

  member x.Metrics(configurator : Func<MetricsConfBuild, MetricsConfBuild>) =
    let builder = MetricsConfBuilder conf

    let built = configurator.Invoke builder
    
    built :?> MetricsConfBuilder
    |> fun builder -> confBuilder builder.conf


  /// Configure a target of the type with a name specified by the parameter name.
  /// The callback, which is the second parameter, lets you configure the target.
  member x.Target<'T when 'T :> SpecificTargetConf>(name : string, f : Func<TargetConfBuild<'T>, TargetConfBuild<'T>>) : ConfBuilder =
    let builderType = typeof<'T>

    let container : ConfBuilderT<'T> =
      { parent              = x
        tr                  = Rule.createForTarget name
        tcSpecific          = None
        useForInternalLog   = false }

    let contRef = ref (container :> TargetConfBuild<_>)

    let parentCc : ParentCallback<_> =
      fun tcSpec ->
        let b = !contRef :?> ConfBuilderT<'T>
        contRef := { b with tcSpecific = Some(tcSpec :?> 'T) } :> TargetConfBuild<_>
        contRef

    let tcSpecific = Activator.CreateInstance(builderType, parentCc) :?> 'T

    contRef := { container with tcSpecific = Some tcSpecific } :> TargetConfBuild<_>

    // escape of the type system to get back to this mutually recursive
    // builder class: hence the comment that the interface TargetConfBuild<_> is not
    // referentially transparent
    let targetConf = f.Invoke(!contRef) :?> ConfBuilderT<'T>

    let tr = 
      if targetConf.useForInternalLog then {targetConf.tr with hiera = Regex(specificHieraForSetInternalLog)}
      else targetConf.tr

    conf
    |> withRule tr
    |> withTarget (targetConf.tcSpecific.Value.Build name)
    |> confBuilder

/// Extensions to make it easier to construct Logary
[<Extension; AutoOpen>]
module FactoryApiExtensions =
  open System
  open Logary
  open Logary.Target.FactoryApi
  open Logary.Configuration

  /// <summary>
  /// Configure the target with default settings.
  /// </summary>
  /// <typeparam name="T">The <see cref="TargetConf"/> to configure
  /// with defaults</typeparam>
  /// <param name="builder"></param>
  /// <returns>The same as input</returns>
  [<Extension; CompiledName "Target">]
  let target<'T when 'T :> SpecificTargetConf> (builder : ConfBuilder) (name : string) =
    builder.Target<'T>(name, new Func<_, _>(id))

/// The main entry point for object oriented languages to interface with Logary,
/// to configure it.
type LogaryFactory =
  /// Configure a new Logary instance. This will also give real targets to the flyweight
  /// targets that have been declared statically in your code. If you call this
  /// you get a log manager that you can later dispose, to shutdown all targets.
  static member New(serviceName : string, configurator : Func<ConfBuilder, ConfBuilder>) : Task<LogManager> =
    if serviceName = null then nullArg "serviceName"
    if configurator = null then nullArg "configurator"
    let config = Config.confLogary serviceName
    let cb = configurator.Invoke (ConfBuilder (config, LogLevel.Error) )
    cb.BuildLogary () |> CSharp.toTask
