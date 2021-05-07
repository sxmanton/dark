module LibBackend.RealExecution

// For executing code with the appropriate production "real" execution, setting
// traces, stdlib, etc, appropriately

open FSharp.Control.Tasks
open System.Threading.Tasks

open Prelude
open Tablecloth

module PT = LibBackend.ProgramTypes
module RT = LibExecution.RuntimeTypes
module AT = LibExecution.AnalysisTypes
module Exe = LibExecution.Execution
module Interpreter = LibExecution.Interpreter

let stdlibFns : Lazy<Map<RT.FQFnName.T, RT.BuiltInFn>> =
  lazy
    (LibExecution.StdLib.StdLib.fns @ LibBackend.StdLib.StdLib.fns
     |> Map.fromListBy (fun fn -> RT.FQFnName.Stdlib fn.name))

let packageFns : Lazy<Task<Map<RT.FQFnName.T, RT.Package.Fn>>> =
  lazy
    (task {
      let! packages = Lazy.force PackageManager.cachedForAPI

      return
        packages
        |> List.map
             (fun (f : PT.Package.Fn) ->
               (RT.FQFnName.Package f.name, PT.Package.toRuntimeType f))
        |> Map.ofList
     })

let libraries : Lazy<Task<RT.Libraries>> =
  lazy
    (task {
      let! packageFns = Lazy.force packageFns
      let stdlibFns = Lazy.force stdlibFns
      // TODO: this keeps a cached version so we're not loading them all the time.
      // Of course, this won't be up to date if we add more functions. This should be
      // some sort of LRU cache.
      return { stdlib = stdlibFns; packageFns = packageFns }
     })


let createState
  (traceID : AT.TraceID)
  (tlid : tlid)
  (program : RT.ProgramContext)
  : Task<RT.ExecutionState * HashSet.T<tlid>> =
  task {
    let canvasID = program.canvasID

    // Any real execution needs to track the touched TLIDs in order to send traces to pusher
    let touchedTLIDs, traceTLIDFn = Exe.traceTLIDs ()
    HashSet.add tlid touchedTLIDs

    let tracing =
      { Exe.noTracing RT.Real with
          storeFnResult = TraceFunctionResults.store canvasID traceID
          storeFnArguments = TraceFunctionArguments.store canvasID traceID
          traceTLID = traceTLIDFn }

    let! libraries = Lazy.force libraries

    return (Exe.createState libraries tracing tlid program, touchedTLIDs)
  }
