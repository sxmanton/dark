module Tests.LibExecution

// Create test cases from .tests files in the tests/stdlib dir

open Expecto

open System.Threading.Tasks
open FSharp.Control.Tasks

open Prelude
open Prelude.Tablecloth
open Tablecloth

module RT = LibExecution.RuntimeTypes
module PT = LibBackend.ProgramTypes
module Exe = LibExecution.Execution

open TestUtils

let t
  (comment : string)
  (code : string)
  (dbs : List<PT.DB.T>)
  (functions : Map<string, PT.UserFunction.T>)
  : Test =
  let name = $"{comment} ({code})"

  if matches @"^\s*$" code || matches @"^\s*//" code then
    ptestTask name { return (Expect.equal "skipped" "skipped" "") }
  else
    testTask name {
      try
        let rtDBs =
          (dbs |> List.map (fun db -> db.name, PT.DB.toRuntimeType db) |> Map.ofList)

        let rtFunctions = functions |> Map.map PT.UserFunction.toRuntimeType

        let! state = executionStateFor name rtDBs rtFunctions

        let source = FSharpToExpr.parse code

        let shouldEqual, actualProg, expectedResult =
          FSharpToExpr.convertToTest source

        let msg = $"\n\n{actualProg}\n=\n{expectedResult} ->"

        let! expected =
          Exe.executeExpr state Map.empty (expectedResult.toRuntimeType ())

        let testOCaml, testFSharp =
          if String.includes "FSHARPONLY" comment then (false, true)
          else if String.includes "OCAMLONLY" comment then (true, false)
          else (true, true)

        if testOCaml then
          let! ocamlActual =
            try
              LibBackend.OCamlInterop.execute
                state.program.accountID
                state.program.canvasID
                actualProg
                Map.empty
                dbs
                (Map.values functions)
            with e ->
              failwith "When calling OCaml code, OCaml server failed: {msg}, {e}"

          if shouldEqual then
            Expect.equalDval
              (normalizeDvalResult ocamlActual)
              expected
              $"OCaml: {msg}"
          else
            Expect.notEqual
              (normalizeDvalResult ocamlActual)
              expected
              $"OCaml: {msg}"

        if testFSharp then
          let! fsharpActual =
            Exe.executeExpr state Map.empty (actualProg.toRuntimeType ())

          let fsharpActual = normalizeDvalResult fsharpActual

          if shouldEqual then
            Expect.equalDval fsharpActual expected $"FSharp: {msg}"
          else
            Expect.notEqual fsharpActual expected $"FSharp: {msg}"



        return ()
      with e ->
        printfn "Exception thrown in test: %s" (e.ToString())
        return (Expect.equal "Exception thrown in test" (e.ToString()) "")
    }


type TestInfo =
  { name : string
    recording : bool
    code : string
    dbs : List<PT.DB.T> }

type TestGroup = { name : string; tests : List<Test>; dbs : List<PT.DB.T> }

type FnInfo =
  { name : string
    recording : bool
    code : string
    tlid : tlid
    parameters : List<PT.UserFunction.Parameter> }

// Read all test files. The test file format is described in README.md
let fileTests () : Test =
  let dir = "tests/testfiles/"

  System.IO.Directory.GetFiles(dir, "*.tests")
  |> Array.filter ((<>) "README.md")
  |> Array.map
       (fun file ->
         let filename = System.IO.Path.GetFileName file
         let emptyTest = { recording = false; name = ""; dbs = []; code = "" }

         let emptyFn =
           { recording = false; name = ""; parameters = []; code = ""; tlid = id 7 }

         let emptyGroup = { name = ""; tests = []; dbs = [] }

         // for recording a multiline test
         let mutable currentTest = emptyTest
         // for recording a multiline function
         let mutable currentFn = emptyFn
         // for recording a bunch if single-line tests grouped together
         let mutable currentGroup = emptyGroup
         let mutable allTests = []
         let mutable functions : Map<string, PT.UserFunction.T> = Map.empty
         let mutable dbs : Map<string, PT.DB.T> = Map.empty

         let finish () =
           if currentTest.recording then
             let newTestCase =
               t currentTest.name currentTest.code currentTest.dbs functions

             allTests <- allTests @ [ newTestCase ]

           if List.length currentGroup.tests > 0 then
             let newTestCase = testList currentGroup.name currentGroup.tests
             allTests <- allTests @ [ newTestCase ]

           if currentFn.recording then
             let (fn : PT.UserFunction.T) =
               { tlid = currentFn.tlid
                 name = currentFn.name
                 nameID = gid ()
                 returnType = PT.TVariable "a"
                 returnTypeID = gid ()
                 description = "test function"
                 infix = false
                 body = (FSharpToExpr.parsePTExpr currentFn.code)
                 parameters = currentFn.parameters }

             functions <- Map.add currentFn.name fn functions

           // Clear settings
           currentTest <- emptyTest
           currentFn <- emptyFn
           currentGroup <- emptyGroup

         (dir + filename)
         |> System.IO.File.ReadLines
         |> Seq.iteri
              (fun i line ->
                let i = i + 1

                // This format is described in testfiles/README.md. If you make
                // any changes, update that file.
                match line with
                // [tests] indicator
                | Regex @"^\[tests\.(.*)\] with DB (.*)$" [ name; dbName ] ->
                    finish ()

                    match Map.get dbName dbs with
                    | Some db -> currentGroup <- { currentGroup with dbs = [ db ] }
                    | None -> failwith $"No DB named {dbName} found"

                    currentGroup <- { currentGroup with name = name }
                | Regex @"^\[tests\.(.*)\]$" [ name ] ->
                    finish ()
                    currentGroup <- { currentGroup with name = name }
                // [db] declaration
                | Regex @"^\[db.(.*) (\{.*\})\]\s*$" [ name; definition ] ->
                    finish ()

                    let (db : PT.DB.T) =
                      { tlid = id i
                        pos = { x = 0; y = 0 }
                        name = name
                        nameID = gid ()
                        version = 0
                        cols =
                          definition
                          |> Json.Vanilla.deserialize<Map<string, string>>
                          |> Map.mapWithIndex
                               (fun k v ->
                                 ({ name = k
                                    nameID = gid ()
                                    typ =
                                      if v = "" then None else Some(PT.DType.parse v)
                                    typeID = gid () } : PT.DB.Col))
                          |> Map.values }

                    dbs <- Map.add name db dbs
                // [function] declaration
                | Regex @"^\[fn\.(\S+) (.*)\]$" [ name; definition ] ->
                    finish ()

                    let parameters : List<PT.UserFunction.Parameter> =
                      definition
                      |> String.split " "
                      |> List.map
                           (fun name ->
                             { name = name
                               nameID = gid ()
                               description = ""
                               typ = Some(PT.TVariable "a")
                               typeID = gid () })

                    currentFn <-
                      { tlid = id i
                        recording = true
                        name = name
                        parameters = parameters
                        code = "" }
                // [test] with DB indicator
                | Regex @"^\[test\.(.*)\] with DB (.*)$" [ name; dbName ] ->
                    finish ()

                    match Map.get dbName dbs with
                    | Some db -> currentTest <- { currentTest with dbs = [ db ] }
                    | None -> failwith $"No DB named {dbName} found"

                    currentTest <-
                      { currentTest with
                          name = $"{name} (line {i})"
                          recording = true }
                // [test] indicator (no DB)
                | Regex @"^\[test\.(.*)\]$" [ name ] ->
                    finish ()

                    currentTest <-
                      { currentTest with
                          name = $"{name} (line {i})"
                          recording = true }
                // Skip whitespace lines
                | Regex @"^\s*$" [] -> ()
                // Skip whole-line comments
                | Regex @"^\s*//.*$" [] when
                  currentTest.recording || currentFn.recording -> ()
                // Append to the current test string
                | _ when currentTest.recording ->
                    currentTest <-
                      { currentTest with code = currentTest.code + line }
                | _ when currentFn.recording ->
                    currentFn <- { currentFn with code = currentFn.code + line }
                // 1-line test
                | Regex @"^(.*)\s*//\s*(.*)$" [ code; comment ] ->
                    let test =
                      t $"{comment} (line {i})" code currentGroup.dbs functions

                    currentGroup <-
                      { currentGroup with tests = currentGroup.tests @ [ test ] }
                | Regex @"^(.*)\s*$" [ code ] ->
                    let test = t $"line {i}" code currentGroup.dbs functions

                    currentGroup <-
                      { currentGroup with tests = currentGroup.tests @ [ test ] }

                | _ -> raise (System.Exception $"can't parse line {i}: {line}"))

         finish ()
         testList $"Tests from {filename}" allTests)
  |> Array.toList
  |> testList "All files"

let tests = lazy (testList "LibExecution" [ fileTests () ])
