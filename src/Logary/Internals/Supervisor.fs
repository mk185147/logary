﻿namespace Logary.Internals

open Logary
open Hopac
open Hopac.Infixes

[<AutoOpen>]
module internal Prelude =
  let always x _ = x
  let inline (^) f x = f x

type Will<'a> = Will of MVar<'a option>

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Will =

  let create (): Will<'a> =
    Will (MVar None)
  let createFull initial: Will<'a> =
    Will (MVar (Some initial))
  let update (Will aM) (a:'a): Alt<unit> =
    MVar.mutateFun (always (Some a)) aM
  let latest (Will aM): Alt<'a option> =
    MVar.read aM
  let exchange (Will aM) (a:'a): Alt<'a option> =
    MVar.modifyFun (fun a' -> Some a, a') aM
  let revoke (Will aM): Alt<unit> =
    MVar.mutateFun (always None) aM

type Policy =
  | Always of FailureAction
  | DetermineWith of (exn -> FailureAction)
  | DetermineWithJob of (exn -> Job<FailureAction>)

and FailureAction =
  | Restart
  | RestartDelayed of restartDelayMs:uint32
  | Terminate
  | Escalate

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Policy =
  let restart = Always Restart
  let restartDelayed delay = Always ^ RestartDelayed delay
  let terminate = Always Terminate
  let escalate = Always Escalate

  let retry (maxRetries: uint32) =
    let cM = MVar 1u
    DetermineWithJob ^ fun _ ->
      MVar.modifyFun (fun r -> r + 1u, r) cM
      >>- fun r ->
        if r = maxRetries then
          Terminate
        else
          Restart

  let retryWithDelay (d: uint32) (maxRetries: uint32) =
    let cM = MVar 1u
    DetermineWithJob ^ fun _ ->
      MVar.modifyFun (fun r -> r + 1u, r) cM
      >>- fun r ->
        if r = maxRetries then
          Terminate
        else
          RestartDelayed d

  let exponentialBackoff (initD: uint32) (mult: uint32) (maxD: uint32) (maxRetries: uint32) =
    let doRestart =
      match initD, mult, maxD with
      | 0u, _, _
      | _, 0u, _ -> always Restart
      | _, _, _ -> fun r ->
        if r = 0u then
          RestartDelayed maxD
        else
          // 2u^31 works, 2u^32 overflows
          let cappedR = min r 31u
          // initD * uint32 close to 2^28 would overflow, sum instead
          RestartDelayed (min maxD (initD + pown mult (int cappedR)))

    let cM = MVar 1u
    DetermineWithJob ^ fun _ ->
      MVar.modifyFun (fun r -> r + 1u, r) cM
      >>- fun r ->
        if r = maxRetries then
          Terminate
        else
          doRestart r

  /// An exponential backoff strategy that sleeps: 100ms, 200ms, 400ms, 800ms,
  /// 1600ms, 3200ms and then maxes out at 6400ms.
  let exponentialBackoffForever =
    exponentialBackoff (* init [ms] *) 100u
                       (* mult *) 2u
                       (* max dur [ms] *) 16000u
                       (* retry indefinitely *) System.UInt32.MaxValue

type SupervisedJob<'a> = Job<Result<'a,exn>>

module Job =
  let rec handleFailureWith (logger: Logger) p act (xJ : #Job<'x>) (ex: exn): SupervisedJob<'x> =
    match act with
    | Restart ->
      logger.eventBP("Exception from supervised job, restarting now.", fun m ->
        m.addExn(ex, Warn))
      >>=. supervise logger p xJ

    | RestartDelayed t ->
      logger.eventBP("Exception from supervised job, restarting in {delay} ms.", fun m ->
        m.setField("delay", Value.Int64 (int64 t))
        m.addExn(ex, Warn))
      >>=. timeOutMillis (int t)
      >>= fun () -> supervise logger p xJ

    | Terminate ->
      logger.eventBP("Exception from supervised job, terminating.", fun m -> m.addExn ex)
      >>-. Result.Error ex

    | Escalate ->
      logger.eventBP ("Exception from supervised job, escalating.", fun m -> m.addExn ex)
      >>=. Job.raises ex

  and makeHandler (logger: Logger) (p: Policy): #Job<'x> -> exn -> SupervisedJob<'x> =
    match p with
    | Always act ->
      handleFailureWith logger p act
    | DetermineWith e2act ->
      fun xJ ex ->
        handleFailureWith logger p (e2act ex) xJ ex
    | DetermineWithJob e2actJ ->
      fun xJ ex ->
        e2actJ ex >>= fun act -> handleFailureWith logger p act xJ ex

  and supervise (logger: Logger) (policy: Policy): #Job<'x> -> SupervisedJob<'x> =
    let handle = makeHandler logger policy
    fun xJ -> Job.tryIn xJ (Result.Ok >> Job.result) (handle xJ)

  let superviseIgnore logger policy xJ =
    supervise logger policy xJ |> Job.Ignore

  let superviseWithWill logger p w2xJ =
    let wl = Will.create ()
    supervise logger p (w2xJ wl)

// Assume:

// In Supervisor
// Policy -> #Job<'x> -> SupervisedJob<'x>
// Policy -> (Will<'a> -> #Job<'x>) -> SupervisedJob<'x>
//

// In TargetConf (module)
// TargetConf.create : (Will<'w> -> (RuntimeInfo * TargetAPI -> Job<unit>))
//                  -> TargetConf

// In TTarget
// TTarget.empty: Target internal state + Will<'a>
// TTarget.create: Target internal config ->
//                  Target internal state + Will<'a> ->
//                  (RuntimeInfo * TargetAPI -> Job<unit>)
// ## call: TTarget -> TargetConf + Will -> :TargetConf


// In Target
//
// Target.create: RuntimeInfo (from Registry)
//             -> name
//             -> TargetConf
//             -> Target.T

// In Registry
// create -> LogaryConf -> RuntimeInfo -> Target.T
// ## call: Target.create: RuntimeInfo -> Target.T
// ## call: Target.T -> Supervisor.superviseWithWill -> SupervisedJob<unit>
// ## call: Target.toService: Target.T * SupervisedJob<unit>
//                          -> Service<Target.T>

// In Config
// Logary config -> internal logger + Policy + name + rules + bufferSize = TargetConf
//               -> Registry
//               -> RuntimeInfo * TargetAPI

// App code -> RuntimeInfo + TTarget.create + TTarget.empty
//          -> Target.