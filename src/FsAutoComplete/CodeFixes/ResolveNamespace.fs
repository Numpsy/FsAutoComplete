module FsAutoComplete.CodeFix.ResolveNamespace

open Ionide.LanguageServerProtocol.Types
open FsAutoComplete.CodeFix
open FsAutoComplete.CodeFix.Types
open FsToolkit.ErrorHandling
open FsAutoComplete.LspHelpers
open FsAutoComplete
open FSharp.Compiler.Text
open FSharp.Compiler.EditorServices

type LineText = string

/// a codefix the provides suggestions for opening modules or using qualified names when an identifier is found that needs qualification
let fix
  (getParseResultsForFile: GetParseResultsForFile)
  (getNamespaceSuggestions:
    ParseAndCheckResults
      -> FcsPos
      -> LineText
      -> Async<CoreResponse<string * list<string * string * InsertionContext * bool> * list<string * string>>>)
  =

  /// insert a line of text at a given line
  let insertLine line lineStr =
    { Range =
        { Start = { Line = line; Character = 0 }
          End = { Line = line; Character = 0 } }
      NewText = lineStr }

  let adjustInsertionPoint (lines: ISourceText) (ctx: InsertionContext) =
    let l = ctx.Pos.Line

    let retVal =
      match ctx.ScopeKind with
      | ScopeKind.TopModule when l > 1 ->
        let line = lines.GetLineString(l - 2)

        let isImplicitTopLevelModule =
          not (line.StartsWith "module" && not (line.EndsWith "="))

        if isImplicitTopLevelModule then 1 else l
      | ScopeKind.TopModule -> 1
      | ScopeKind.Namespace ->
        let mostRecentNamespaceInScope =
          let lineNos = if l = 0 then [] else [ 0 .. l - 1 ]

          lineNos
          |> List.mapi (fun i line -> i, lines.GetLineString line)
          |> List.choose (fun (i, lineStr) -> if lineStr.StartsWith "namespace" then Some i else None)
          |> List.tryLast

        match mostRecentNamespaceInScope with
        // move to the next line below "namespace" and convert it to F# 1-based line number
        | Some line -> line + 2
        | None -> l
      | _ -> l

    let containsAttribute (x: string) = x.Contains "[<"
    let currentLine = System.Math.Max(retVal - 2, 0) |> lines.GetLineString

    if currentLine |> containsAttribute then
      retVal + 1
    else
      retVal

  let qualifierFix file diagnostic qual =
    { SourceDiagnostic = Some diagnostic
      Edits =
        [| { Range = diagnostic.Range
             NewText = qual } |]
      File = file
      Title = $"Use %s{qual}"
      Kind = FixKind.Fix }

  let openFix (text: ISourceText) file diagnostic (word: string) (ns, name: string, ctx, multiple) : Fix =
    let insertPoint = adjustInsertionPoint text ctx
    let docLine = insertPoint - 1

    let actualOpen =
      if name.EndsWith word && name <> word then
        let prefix = name.Substring(0, name.Length - word.Length).TrimEnd('.')

        $"%s{ns}.%s{prefix}"
      else
        ns

    let lineStr =
      let whitespace =
        let column =
          // HACK: This is a work around for inheriting the correct column of the current module
          // It seems the column we get from FCS is incorrect
          let previousLine = docLine - 1
          let insertionPointIsNotOutOfBoundsOfTheFile = docLine > 0

          let theThereAreOtherOpensInThisModule () =
            text.GetLineString(previousLine).Contains "open "

          if insertionPointIsNotOutOfBoundsOfTheFile && theThereAreOtherOpensInThisModule () then
            text.GetLineString(previousLine).Split("open") |> Seq.head |> Seq.length // inherit the previous opens whitespace
          else
            ctx.Pos.Column

        String.replicate column " "

      $"%s{whitespace}open %s{actualOpen}\n"

    let edits = [| yield insertLine docLine lineStr |]

    { Edits = edits
      File = file
      SourceDiagnostic = Some diagnostic
      Title = $"open %s{actualOpen}"
      Kind = FixKind.Fix }

  Run.ifDiagnosticByMessage "is not defined" (fun diagnostic codeActionParameter ->
    asyncResult {
      let pos = protocolPosToPos diagnostic.Range.Start

      let filePath = codeActionParameter.TextDocument.GetFilePath() |> Utils.normalizePath

      let! tyRes, line, lines = getParseResultsForFile filePath pos

      match! getNamespaceSuggestions tyRes pos line with
      | CoreResponse.InfoRes msg
      | CoreResponse.ErrorRes msg -> return []
      | CoreResponse.Res(word, opens, qualifiers) ->
        let quals =
          qualifiers
          |> List.map (fun (_, qual) -> qualifierFix codeActionParameter.TextDocument diagnostic qual)

        let ops =
          opens
          |> List.map (openFix lines codeActionParameter.TextDocument diagnostic word)

        return [ yield! ops; yield! quals ]
    })
