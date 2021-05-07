module Tests.All

// Main entry point for tests being run

open Expecto
open System.Threading.Tasks

[<EntryPoint>]
let main args =
  LibBackend.Init.init "Tests" // Must go before Tests.BwdServer.init
  let (_ : Task) = Tests.BwdServer.init ()
  let (_ : Task) = Tests.HttpClient.init ()
  LibBackend.Migrations.init ()
  LibService.Telemetry.Console.loadTelemetry ()
  (LibBackend.Account.initTestAccounts ()).Wait()

  let tests =
    [ Tests.Account.tests
      Tests.ApiServer.tests
      Tests.Authorization.tests
      Tests.BwdServer.tests
      Tests.Canvas.tests
      Tests.Cron.tests
      Tests.DvalRepr.tests
      Tests.EventQueue.tests
      Tests.Execution.tests
      Tests.FSharpToExpr.tests
      Tests.LibExecution.tests.Force()
      Tests.HttpClient.tests
      Tests.OCamlInterop.tests
      Tests.Prelude.tests
      Tests.ProgramTypes.tests
      Tests.Routing.tests
      Tests.StdLib.tests
      Tests.SqlCompiler.tests
      Tests.Traces.tests
      Tests.TypeChecker.tests
      Tests.Undo.tests
      Tests.UserDB.tests ]

  // this does async stuff within it, so do not run it from a task/async
  // context or it may hang
  let result = runTestsWithCLIArgs [] args (testList "tests" tests)
  if result <> 0 then failwith "Tests have non-zero exit code"
  0
