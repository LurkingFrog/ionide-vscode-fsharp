namespace Ionide.VSCode.FSharp

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.Import
open Fable.Import.vscode
open Fable.Import.Node

open DTO
open Ionide.VSCode.Helpers
open System.Text.RegularExpressions


module Errors =
    let private logger = ConsoleAndOutputChannelLogger(Some "Errors", Level.DEBUG, None, Some Level.DEBUG)

    let mutable private currentDiagnostic = languages.createDiagnosticCollection ()

    let private mapResult (ev : ParseResult) =
        let errors =
            ev.Data.Errors
            |> Seq.distinctBy (fun error -> error.Severity, error.StartLine, error.StartColumn)
            |> Seq.choose (fun error ->
                try
                    if error.FileName |> String.startWith "\\" then None else
                    let range = CodeRange.fromError error
                    let loc = Location (Uri.file error.FileName, range |> Case1)
                    let severity = if error.Severity = "Error" then 0 else 1
                    (Diagnostic(range, error.Message, unbox severity), error.FileName) |> Some
                with
                | _ -> None )
            |> ResizeArray
        ev.Data.File, errors

    type DocumentParsedEvent = {
        fileName: string
        text: string
        version: float
        /// BEWARE: Live object, might have changed since the parsing
        document: TextDocument
        result: ParseResult
    }

    let private onDocumentParsedEmitter = EventEmitter<DocumentParsedEvent>()
    let onDocumentParsed = onDocumentParsedEmitter.event;

    let private parse (document : TextDocument) =
        let fileName = document.fileName
        let text = document.getText()
        let version = document.version
        LanguageService.parse document.fileName (document.getText()) document.version
        |> Promise.map (fun (result : ParseResult) ->
            if isNotNull result then
                onDocumentParsedEmitter.fire { fileName = fileName; text = text; version = version; document = document; result = result }
                // printf "CodeLens - File parsed"
                CodeLens.refresh.fire (unbox version)
                Linter.refresh.fire fileName
                (Uri.file fileName, (mapResult result |> snd |> Seq.map fst |> ResizeArray)) |> currentDiagnostic.set  )

    let private parseFile (file : TextDocument) =
        match file with
        | Document.FSharp ->
            let path = file.fileName
            let prom = Project.find path
            match prom with
            | Some p -> p
                        |> Project.load
                        |> Promise.bind (fun _ -> parse file)
            | None -> parse file
        | _ -> Promise.lift (null |> unbox)

    let mutable private timer = None

    let private handler (event : TextDocumentChangeEvent) =
        timer |> Option.iter(clearTimeout)
        timer <- Some (setTimeout (fun _ ->
            match event.document with
            | Document.FSharp ->
                parse event.document
                |> ignore
            | _ -> () ) 1000.)

    let private handlerSave (doc : TextDocument) =
        match doc with
        | Document.FSharp ->
            promise {
                let! (res : ParseResult) = LanguageService.parseProjects doc.fileName
                if isNotNull res then
                    let (_,mapped) = res |> mapResult
                    currentDiagnostic.clear ()
                    mapped
                    |> Seq.groupBy snd
                    |> Seq.iter (fun (fn, errors) ->
                        let errs = errors |> Seq.map fst |> ResizeArray
                        currentDiagnostic.set(Uri.file fn, errs) )
            }
        | _ -> Promise.empty

    let private handlerOpen (event : TextEditor) =
        if JS.isDefined event then
            parseFile event.document
        else
            Promise.lift ()

    // let private handleNotification res =
    //     res
    //     |> Array.map mapResult
    //     |> Array.iter (fun (file, errors) ->
    //         if window.activeTextEditor.document.fileName <> file then
    //             currentDiagnostic.set(Uri.file file, errors |> Seq.map fst |> ResizeArray))

    let activate (context: ExtensionContext) =
        workspace.onDidChangeTextDocument $ (handler,(), context.subscriptions) |> ignore
        workspace.onDidSaveTextDocument $ (handlerSave , (), context.subscriptions) |> ignore
        window.onDidChangeActiveTextEditor $ (handlerOpen, (), context.subscriptions) |> ignore
        //LanguageService.registerNotify handleNotification

        match window.visibleTextEditors |> Seq.toList with
        | [] -> Promise.lift (null |> unbox)
        | [x] -> parseFile x.document
                 |> Promise.onSuccess (fun _ -> handlerSave x.document |> ignore)
        | x::tail ->
            tail
            |> List.fold (fun acc e -> acc |> Promise.bind(fun _ -> parseFile e.document ) )
               (parseFile x.document )
            |> Promise.onSuccess (fun _ -> handlerSave x.document |> ignore)


