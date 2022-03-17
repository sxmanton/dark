module LibBackend.Init

// LibBackend holds the whole framework

open System.Threading.Tasks
open FSharp.Control.Tasks

open Npgsql.FSharp
open Db

open Prelude
open Microsoft.Extensions.Diagnostics.HealthChecks

module Telemetry = LibService.Telemetry

// Add a startup probe to check if we can check the tier one canvases
let legacyServerCheck : LibService.Kubernetes.HealthCheck =
  { probeTypes = [ LibService.Kubernetes.Startup ]
    name = "legacyServerCheck"
    checkFn =
      fun (_ : System.Threading.CancellationToken) ->
        task {
          try
            // Make sure we can load a canvas
            // Loading all the tier one canvases takes way too long, so just pick a simple one
            let host = CanvasName.create "ian-httpbin"
            let! meta = Canvas.getMeta host
            let! (_ : Canvas.T) = Canvas.loadAll meta
            return HealthCheckResult.Healthy("It's fine")
          with
          | e -> return HealthCheckResult.Unhealthy(e.Message)
        } }



let waitForDB () : Task<unit> =
  task {
    use (span : Telemetry.Span.T) = Telemetry.createRoot "wait for db"
    let mutable success = false
    let mutable count = 0
    Telemetry.addEvent "starting to loop to wait for DB" []
    while not success do
      use (span : Telemetry.Span.T) = Telemetry.child "iteration" [ "count", count ]
      try
        count <- count + 1
        let! date =
          Sql.query "select current_date"
          |> Sql.parameters []
          |> Sql.executeRowAsync (fun read -> read.string "current_date")
        Telemetry.addTag "date" date
        success <- true
      with
      | e ->
        Telemetry.addException e
        do! Task.Delay 1000
    return ()
  }



let init (serviceName : string) (runSideEffects : bool) : Task<unit> =
  task {
    print $"Initing LibBackend in {serviceName}"
    Db.init ()

    Json.OCamlCompatible.registerConverter (
      EventQueue.WorkerStates.JsonConverter.WorkerStateConverter()
    )

    Json.Vanilla.registerConverter (
      EventQueue.WorkerStates.STJJsonConverter.WorkerStateConverter()
    )

    if runSideEffects then do! Account.init serviceName

    print $" Inited LibBackend in {serviceName}"
  }
