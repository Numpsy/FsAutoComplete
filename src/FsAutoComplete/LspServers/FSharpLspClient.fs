namespace FsAutoComplete.Lsp


open Ionide.LanguageServerProtocol
open Ionide.LanguageServerProtocol.Types.LspResult
open Ionide.LanguageServerProtocol.Server
open Ionide.LanguageServerProtocol.Types
open FsAutoComplete.LspHelpers
open System
open System.Threading.Tasks
open FsAutoComplete.Utils
open System.Threading
open IcedTasks


type FSharpLspClient(sendServerNotification: ClientNotificationSender, sendServerRequest: ClientRequestSender) =

  inherit LspClient()

  member val ClientCapabilities: ClientCapabilities option = None with get, set

  override __.WindowShowMessage(p) =
    sendServerNotification "window/showMessage" (box p) |> Async.Ignore

  override __.WindowShowMessageRequest(p) =
    sendServerRequest.Send "window/showMessageRequest" (box p)

  override __.WindowLogMessage(p) =
    sendServerNotification "window/logMessage" (box p) |> Async.Ignore

  override __.TelemetryEvent(p) =
    sendServerNotification "telemetry/event" (box p) |> Async.Ignore

  override __.ClientRegisterCapability(p) =
    sendServerRequest.Send "client/registerCapability" (box p)

  override __.ClientUnregisterCapability(p) =
    sendServerRequest.Send "client/unregisterCapability" (box p)

  override __.WorkspaceWorkspaceFolders() =
    sendServerRequest.Send "workspace/workspaceFolders" ()

  override __.WorkspaceConfiguration(p) =
    sendServerRequest.Send "workspace/configuration" (box p)

  override __.WorkspaceApplyEdit(p) =
    sendServerRequest.Send "workspace/applyEdit" (box p)

  override __.WorkspaceSemanticTokensRefresh() =
    sendServerNotification "workspace/semanticTokens/refresh" () |> Async.Ignore

  override __.TextDocumentPublishDiagnostics(p: PublishDiagnosticsParams) =
    sendServerNotification "textDocument/publishDiagnostics" (box p) |> Async.Ignore

  ///Custom notification for workspace/solution/project loading events
  member __.NotifyWorkspace(p: PlainNotification) =
    sendServerNotification "fsharp/notifyWorkspace" (box p) |> Async.Ignore

  ///Custom notification for initial workspace peek
  member __.NotifyWorkspacePeek(p: PlainNotification) =
    sendServerNotification "fsharp/notifyWorkspacePeek" (box p) |> Async.Ignore

  member __.NotifyCancelledRequest(p: PlainNotification) =
    sendServerNotification "fsharp/notifyCancel" (box p) |> Async.Ignore

  member __.NotifyFileParsed(p: PlainNotification) =
    sendServerNotification "fsharp/fileParsed" (box p) |> Async.Ignore

  member __.NotifyDocumentAnalyzed(p: DocumentAnalyzedNotification) =
    sendServerNotification "fsharp/documentAnalyzed" (box p) |> Async.Ignore

  member __.NotifyTestDetected(p: TestDetectedNotification) =
    sendServerNotification "fsharp/testDetected" (box p) |> Async.Ignore

  member x.CodeLensRefresh() =
    match x.ClientCapabilities with
    | Some { Workspace = Some { CodeLens = Some { RefreshSupport = Some true } } } ->
      sendServerNotification "workspace/codeLens/refresh" () |> Async.Ignore
    | _ -> async { return () }

  override x.WorkDoneProgressCreate(token) =
    match x.ClientCapabilities with
    | Some { Window = Some { workDoneProgress = Some true } } ->
      let progressCreate: WorkDoneProgressCreateParams = { token = token }
      sendServerRequest.Send "window/workDoneProgress/create" (box progressCreate)
    | _ -> async { return Error(JsonRpc.Error.InternalErrorMessage "workDoneProgress is disabled") }

  override x.Progress(token, value) =
    let progress: ProgressParams<_> = { token = token; value = value }
    sendServerNotification "$/progress" (box progress) |> Async.Ignore



/// <summary>
/// An awaitable wrapper around a task whose result is disposable. The wrapper is not disposable, so this prevents usage errors like "use _lock = myAsync()" when the appropriate usage should be "use! _lock = myAsync())".
/// </summary>
[<Struct>]
type AwaitableDisposable<'T when 'T :> IDisposable>(t: Task<'T>) =
  member x.GetAwaiter() = t.GetAwaiter()
  member x.AsTask() = t
  static member op_Implicit(source: AwaitableDisposable<'T>) = source.AsTask()

[<AutoOpen>]
module private SemaphoreSlimExtensions =
  // Based on https://gist.github.com/StephenCleary/7dd1c0fc2a6594ba0ed7fb7ad6b590d6
  // and https://gist.github.com/brendankowitz/5949970076952746a083054559377e56
  type SemaphoreSlim with

    member x.LockAsync(?ct: CancellationToken) =
      AwaitableDisposable(
        task {
          let ct = defaultArg ct CancellationToken.None
          let t = x.WaitAsync(ct)

          do! t

          return
            { new IDisposable with
                member _.Dispose() =
                  // only release if the task completed successfully
                  // otherwise, we could be releasing a semaphore that was never acquired
                  if t.Status = TaskStatus.RanToCompletion then
                    x.Release() |> ignore }
        }
      )

type ServerProgressReport(lspClient: FSharpLspClient, ?token: ProgressToken) =

  let mutable canReportProgress = false
  let mutable endSent = false

  let locker = new SemaphoreSlim(1, 1)

  member val Token = defaultArg token (ProgressToken.Second((Guid.NewGuid().ToString())))

  member x.Begin(title, ?cancellable, ?message, ?percentage) =
    cancellableTask {
      use! __ = fun ct -> locker.LockAsync(ct)

      if not endSent then
        let! result = lspClient.WorkDoneProgressCreate x.Token

        match result with
        | Ok() -> canReportProgress <- true
        | Error e -> canReportProgress <- false

        if canReportProgress then
          do!
            lspClient.Progress(
              x.Token,
              WorkDoneProgressBegin.Create(
                title,
                ?cancellable = cancellable,
                ?message = message,
                ?percentage = percentage
              )
            )
    }

  member x.Report(?cancellable, ?message, ?percentage) =
    cancellableTask {
      use! __ = fun ct -> locker.LockAsync(ct)

      if canReportProgress && not endSent then
        do!
          lspClient.Progress(
            x.Token,
            WorkDoneProgressReport.Create(?cancellable = cancellable, ?message = message, ?percentage = percentage)
          )
    }

  member x.End(?message) =
    cancellableTask {
      use! __ = fun ct -> locker.LockAsync(ct)
      let stillNeedsToSend = canReportProgress && not endSent
      endSent <- true

      if stillNeedsToSend then
        do! lspClient.Progress(x.Token, WorkDoneProgressEnd.Create(?message = message))
    }

  interface IAsyncDisposable with
    member x.DisposeAsync() =
      task { do! x.End () (CancellationToken.None) } |> ValueTask

  interface IDisposable with
    member x.Dispose() =
      (x :> IAsyncDisposable).DisposeAsync() |> ignore


open System.Diagnostics.Tracing
open System.Collections.Concurrent
open System.Diagnostics
open Ionide.ProjInfo.Logging


/// <summary>listener for the the events generated from the fsc ActivitySource</summary>
type ProgressListener(lspClient: FSharpLspClient, traceNamespace: string array) =

  let isOneOf list string =
    list |> Array.exists (fun f -> f string)

  let strEquals (other: string) (this: string) =
    this.Equals(other, StringComparison.InvariantCultureIgnoreCase)

  let strContains (substring: string) (str: string) = str.Contains(substring)

  let interestingActivities = traceNamespace |> Array.map strContains

  let logger = LogProvider.getLoggerByName "Compiler"

  let mutable isDisposed = false

  let inflightEvents =
    ConcurrentDictionary<_, System.Diagnostics.Activity * ServerProgressReport>()

  let isStopped (activity: Activity) =
#if NET7_0_OR_GREATER
    activity.IsStopped
    ||
#endif
    // giving this 2 seconds to report something, otherwise assume it's a dead activity
    ((DateTime.UtcNow - activity.StartTimeUtc) > TimeSpan.FromSeconds(2.)
     && activity.Duration = TimeSpan.Zero)

  let getTagItemSafe key (a: Activity) = a.GetTagItem key |> Option.ofObj

  let getFileName =
    getTagItemSafe Tracing.SemanticConventions.FCS.fileName
    >> Option.map string
    >> Option.map IO.Path.GetFileName
    >> Option.defaultValue String.Empty

  let getProject =
    getTagItemSafe Tracing.SemanticConventions.FCS.project
    >> Option.map string
    >> Option.map IO.Path.GetFileName
    >> Option.defaultValue String.Empty

  let getUserOpName =
    getTagItemSafe Tracing.SemanticConventions.FCS.userOpName
    >> Option.map string
    >> Option.defaultValue String.Empty

  let mbp =
    MailboxProcessor.Start(fun inbox ->
      async {
        while not isDisposed do
          for (a, p) in inflightEvents.Values do
            // We don't get always get a corresponding ActivityStopped for a given Activity event so this looks at anything that hasn't had a duration incresed at all
            // We don't seem to get a corresponding ActivityStopped on cancelled typechecks.
            if isStopped a then
              do! p.End()
              inflightEvents.TryRemove(a.Id) |> ignore
            else
              // FSC doesn't start their spans with tags so we have to see if it's been added later https://github.com/dotnet/fsharp/issues/14776
              let message =
                [ getFileName a; getProject a; getUserOpName a ]
                |> List.filter (String.IsNullOrEmpty >> not)
                |> String.join " - "

              do! p.Report(message = message)

          match! inbox.TryReceive(250) with
          | None ->
            // if we don't get an event in 250 ms just loop again so we can analyze inflightEvents
            ()
          | Some(action, activity: Activity) ->

            match action with
            | "start" ->
              let fileName = getFileName activity
              let userOpName = getUserOpName activity

              logger.trace (
                Log.setMessageI
                  $"Started : {activity.DisplayName:DisplayName} - {userOpName:UserOpName} - {fileName:fileName}"
              )

              if
                activity.DisplayName |> isOneOf interestingActivities
                && not (isStopped activity)
              then
                let progressReport = new ServerProgressReport(lspClient)

                if inflightEvents.TryAdd(activity.Id, (activity, progressReport)) then

                  do! progressReport.Begin($"{activity.DisplayName}")

            | "stop" ->
              let fileName = getFileName activity
              let userOpName = getUserOpName activity
              let duration = activity.Duration.ToString()

              logger.trace (
                Log.setMessageI
                  $"Finished : {activity.DisplayName:DisplayName} - {userOpName:UserOpName} - {fileName:fileName} - took {duration:duration}"
              )

              if activity.DisplayName |> isOneOf interestingActivities then
                match inflightEvents.TryRemove(activity.Id) with
                | true, (old, progressReport) -> do! progressReport.End()
                | _ -> ()

            | _ -> ()

      })


  let shouldListenTo (act: ActivitySource) = act.Name = Tracing.fscServiceName

  let activityStarted (act: Activity) = mbp.Post("start", act)

  let activityStopped (act: Activity) = mbp.Post("stop", act)

  let listener =
    new ActivityListener(
      ShouldListenTo = shouldListenTo,
      Sample = (fun _ -> ActivitySamplingResult.AllDataAndRecorded),
      ActivityStarted = activityStarted,
      ActivityStopped = activityStopped
    )

  do ActivitySource.AddActivityListener listener

  interface IDisposable with
    member this.Dispose() : unit =
      (this :> IAsyncDisposable).DisposeAsync() |> ignore

  interface IAsyncDisposable with
    member this.DisposeAsync() : ValueTask =
      // was getting a compile error for the statemachine in CI to `task`
      async {
        if not isDisposed then
          isDisposed <- true
          dispose listener

          for (a, p) in inflightEvents.Values do
            do! (disposeAsync p).AsTask() |> Async.AwaitTask
            inflightEvents.TryRemove(a.Id) |> ignore
      }
      |> Async.StartImmediateAsTask
      |> ValueTask
