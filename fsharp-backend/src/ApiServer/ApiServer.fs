module ApiServer.ApiServer

open System
open Microsoft.AspNetCore
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.FileProviders
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type StringValues = Microsoft.Extensions.Primitives.StringValues

open System.Threading.Tasks
open FSharp.Control.Tasks

open Prelude
open Tablecloth

open Http
open Middleware
open Microsoft.AspNetCore.StaticFiles

module Auth = LibBackend.Authorization
module Config = LibBackend.Config

module FireAndForget = LibService.FireAndForget
module Kubernetes = LibService.Kubernetes
module Rollbar = LibService.Rollbar
module Telemetry = LibService.Telemetry

type Packages = List<LibExecution.ProgramTypes.Package.Fn>

// --------------------
// Handlers
// --------------------
let addRoutes
  (packages : Packages)
  (app : IApplicationBuilder)
  : IApplicationBuilder =
  let ab = app
  let app = app :?> WebApplication

  let R = Some Auth.Read
  let RW = Some Auth.ReadWrite

  let builder = RouteBuilder(ab)

  // This route is used so that we know that the http proxy is actually proxying the server
  let checkApiserver : HttpHandler =
    (fun (ctx : HttpContext) ->
      task { return ctx.Response.WriteAsync("success: this is apiserver") })

  let addRoute
    (verb : string)
    (pattern : string)
    (middleware : HttpMiddleware)
    (perm : Option<Auth.Permission>)
    (handler : HttpHandler)
    =
    builder.MapMiddlewareVerb(
      verb,
      pattern,
      (fun appBuilder ->
        appBuilder.UseMiddleware(middleware)
        // Do this inside the other middleware so we still get canvas, etc
        Option.tap (fun perm -> appBuilder.UseMiddleware(canvasMiddleware perm)) perm
        appBuilder.Run(handler))
    )
    |> ignore<IRouteBuilder>

  let std = standardMiddleware
  let html = htmlMiddleware

  let clientJsonGETApi name perm f =
    let handler = clientJsonHandler f
    let route = $"/api/{{canvasName}}/{name}"
    addRoute "GET" route std perm handler

  let clientJsonApi name perm f =
    let handler = clientJsonHandler f
    let route = $"/api/{{canvasName}}/{name}"
    addRoute "POST" route std perm handler

  let clientJsonApiOption name perm f =
    let handler = clientJsonOptionHandler f
    let route = $"/api/{{canvasName}}/{name}"
    addRoute "POST" route std perm handler

  addRoute "GET" "/login" html None Login.loginPage
  addRoute "POST" "/login" html None Login.loginHandler
  addRoute "GET" "/logout" html None Login.logout
  addRoute "POST" "/logout" html None Login.logout

  // Provide an unauthenticated route to check the server
  builder.MapGet("/check-apiserver", checkApiserver) |> ignore<IRouteBuilder>

  addRoute "GET" "/a/{canvasName}" html R (htmlHandler Ui.uiHandler)

  // For internal testing - please don't test this out, it might page me
  let exceptionFn (ctx : HttpContext) =
    let userInfo = loadUserInfo ctx
    Exception.raiseInternal "triggered test exception" [ "user", userInfo.username ]
  addRoute "GET" "/a/{canvasName}/trigger-exception" std R exceptionFn

  clientJsonApi "v1/add_op" RW AddOps.V1.addOp
  clientJsonApi "register_tunnel" RW Tunnels.Register.register
  clientJsonApi "all_traces" R Traces.AllTraces.fetchAll
  clientJsonApi "delete_404" RW F404s.Delete.delete
  clientJsonApiOption "delete-toplevel-forever" RW Toplevels.Delete.delete
  clientJsonApi "v1/delete_secret" RW Secrets.DeleteV1.delete
  clientJsonApi "v1/execute_function" RW Execution.FunctionV1.execute
  clientJsonApi "get_404s" R F404s.List.get
  clientJsonApi "v1/get_db_stats" R DBs.DBStatsV1.getStats
  clientJsonApiOption "v1/get_trace_data" R Traces.TraceDataV1.getTraceData
  clientJsonApi "get_unlocked_dbs" R DBs.Unlocked.get
  clientJsonApi "get_worker_stats" R Workers.WorkerStats.getStats
  clientJsonApi "v1/initial_load" R InitialLoad.V1.initialLoad
  clientJsonGETApi "v1/initial_load" R InitialLoad.V1.initialLoad
  clientJsonApi "v1/insert_secret" RW Secrets.InsertV1.insert
  clientJsonApi "v1/packages" R (Packages.ListV1.packages packages)
  clientJsonGETApi "v1/packages" R (Packages.ListV1.packages packages)
  // CLEANUP: packages/upload_function
  // CLEANUP: save_test handler
  clientJsonApi "v1/trigger_handler" RW Execution.HandlerV1.trigger
  clientJsonApi "worker_schedule" RW Workers.Scheduler.updateSchedule

  app.UseRouter(builder.Build())


// --------------------
// Setup web server
// --------------------
let configureStaticContent (app : IApplicationBuilder) : IApplicationBuilder =
  if Config.apiServerServeStaticContent then
    let contentTypeProvider = FileExtensionContentTypeProvider()
    // See also scripts/deployment/_push-assets-to-cdn
    contentTypeProvider.Mappings[ ".wasm" ] <- "application/wasm"
    contentTypeProvider.Mappings[ ".pdb" ] <- "text/plain"
    contentTypeProvider.Mappings[ ".dll" ] <- "application/octet-stream"
    contentTypeProvider.Mappings[ ".dat" ] <- "application/octet-stream"
    contentTypeProvider.Mappings[ ".blat" ] <- "application/octet-stream"

    app.UseStaticFiles(
      StaticFileOptions(
        ServeUnknownFileTypes = true,
        FileProvider = new PhysicalFileProvider(Config.webrootDir),
        OnPrepareResponse =
          (fun ctx ->
            ctx.Context.Response.Headers[ "Access-Control-Allow-Origin" ] <-
              StringValues([| "*" |])),
        ContentTypeProvider = contentTypeProvider
      )
    )
  else
    app

let rollbarCtxToMetadata (ctx : HttpContext) : (Rollbar.Person * Metadata) =
  let person =
    try
      loadUserInfo ctx |> LibBackend.Account.userInfoToPerson
    with
    | _ -> None
  let canvas =
    try
      string (loadCanvasInfo ctx).name
    with
    | _ -> null
  (person, [ "canvas", canvas ])

let configureApp (packages : Packages) (appBuilder : WebApplication) =
  appBuilder
  |> fun app -> app.UseServerTiming() // must go early or this is dropped
  |> fun app -> Rollbar.AspNet.addRollbarToApp app rollbarCtxToMetadata None
  |> fun app -> app.UseHttpsRedirection()
  |> fun app -> app.UseHsts()
  |> fun app -> app.UseRouting()
  // must go after UseRouting
  |> Kubernetes.configureApp LibService.Config.apiServerKubernetesPort
  |> configureStaticContent
  |> addRoutes packages
  |> ignore<IApplicationBuilder>

// A service is a value that's added to each request, to be used by some middleware.
// For example, ServerTiming adds a ServerTiming value that is then used by the ServerTiming middleware
let configureServices (services : IServiceCollection) : unit =
  services
  |> Rollbar.AspNet.addRollbarToServices
  |> Telemetry.AspNet.addTelemetryToServices "ApiServer" Telemetry.TraceDBQueries
  |> Kubernetes.configureServices []
  |> fun s -> s.AddServerTiming()
  |> fun s -> s.AddHsts(LibService.HSTS.setConfig)
  |> ignore<IServiceCollection>

let webserver
  (packages : Packages)
  (loggerSetup : ILoggingBuilder -> unit)
  (httpPort : int)
  (healthCheckPort : int)
  : WebApplication =
  let hcUrl = Kubernetes.url healthCheckPort

  let builder = WebApplication.CreateBuilder()
  configureServices builder.Services
  LibService.Kubernetes.registerServerTimeout builder.WebHost

  builder.WebHost
  |> fun wh -> wh.ConfigureLogging(loggerSetup)
  |> fun wh -> wh.UseKestrel(LibService.Kestrel.configureKestrel)
  |> fun wh -> wh.UseUrls(hcUrl, $"http://darklang.localhost:{httpPort}")
  |> ignore<IWebHostBuilder>

  let app = builder.Build()
  configureApp packages app
  app

let run (packages : Packages) : unit =
  let port = LibService.Config.apiServerPort
  let k8sPort = LibService.Config.apiServerKubernetesPort
  (webserver packages LibService.Logging.noLogger port k8sPort).Run()

let initSerializers () =
  Json.Vanilla.allow<AddOps.V1.Params> "ApiServer.AddOps"
  Json.Vanilla.allow<AddOps.V1.T> "ApiServer.AddOps"
  Json.Vanilla.allow<DBs.DBStatsV1.Params> "ApiServer.DBs"
  Json.Vanilla.allow<DBs.DBStatsV1.T> "ApiServer.DBs"
  Json.Vanilla.allow<DBs.Unlocked.T> "ApiServer.DBs"
  Json.Vanilla.allow<Execution.FunctionV1.Params> "ApiServer.Execution"
  Json.Vanilla.allow<Execution.FunctionV1.T> "ApiServer.Execution"
  Json.Vanilla.allow<Execution.HandlerV1.Params> "ApiServer.Execution"
  Json.Vanilla.allow<Execution.HandlerV1.T> "ApiServer.Execution"
  Json.Vanilla.allow<F404s.Delete.Params> "ApiServer.F404s"
  Json.Vanilla.allow<F404s.Delete.T> "ApiServer.F404s"
  Json.Vanilla.allow<F404s.List.T> "ApiServer.F404s"
  Json.Vanilla.allow<List<Functions.BuiltInFn.T>> "ApiServer.Functions"
  Json.Vanilla.allow<InitialLoad.V1.T> "ApiServer.InitialLoad"
  Json.Vanilla.allow<Packages.ListV1.T> "ApiServer.Packages"
  Json.Vanilla.allow<Secrets.DeleteV1.Params> "ApiServer.Secrets"
  Json.Vanilla.allow<Secrets.DeleteV1.T> "ApiServer.Secrets"
  Json.Vanilla.allow<Secrets.InsertV1.Params> "ApiServer.Secrets"
  Json.Vanilla.allow<Secrets.InsertV1.T> "ApiServer.Secrets"
  Json.Vanilla.allow<Toplevels.Delete.Params> "ApiServer.Toplevels"
  Json.Vanilla.allow<Toplevels.Delete.T> "ApiServer.Toplevels"
  Json.Vanilla.allow<Traces.AllTraces.T> "ApiServer.Traces"
  Json.Vanilla.allow<Traces.TraceDataV1.Params> "ApiServer.Traces"
  Json.Vanilla.allow<Traces.TraceDataV1.T> "ApiServer.Traces"
  Json.Vanilla.allow<Tunnels.Register.Params> "ApiServer.Tunnels"
  Json.Vanilla.allow<Tunnels.Register.T> "ApiServer.Tunnels"
  Json.Vanilla.allow<Workers.Scheduler.Params> "ApiServer.Workers"
  Json.Vanilla.allow<Workers.Scheduler.T> "ApiServer.Workers"
  Json.Vanilla.allow<Workers.WorkerStats.Params> "ApiServer.Workers"
  Json.Vanilla.allow<Workers.WorkerStats.T> "ApiServer.Workers"
  Json.Vanilla.allow<Map<string, string>> "ApiServer.UI"



[<EntryPoint>]
let main _ =
  try
    let name = "ApiServer"
    print "Starting ApiServer"
    Prelude.init ()
    LibService.Init.init name
    LibExecution.Init.init ()
    (LibBackend.Init.init LibBackend.Init.WaitForDB name).Result
    (LibRealExecution.Init.init name).Result

    if Config.createAccounts then
      LibBackend.Account.initializeDevelopmentAccounts(name).Result

    initSerializers ()

    let packages = LibBackend.PackageManager.allFunctions().Result
    run packages
    // CLEANUP I suspect this isn't called
    LibService.Init.shutdown name
    0
  with
  | e -> LibService.Rollbar.lastDitchBlockAndPage "Error starting ApiServer" e
