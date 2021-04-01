module ApiServer.Api

// Functions and API endpoints for the API

open Microsoft.AspNetCore.Http
open Giraffe
open Giraffe.EndpointRouting

open System.Threading.Tasks
open FSharp.Control.Tasks
open FSharpPlus
open Prelude
open Tablecloth

open Npgsql.FSharp.Tasks
open Npgsql
open LibBackend.Db

module PT = LibBackend.ProgramTypes
module OT = LibBackend.OCamlInterop.OCamlTypes
module ORT = LibBackend.OCamlInterop.OCamlTypes.RuntimeT
module AT = LibExecution.AnalysisTypes
module Convert = LibBackend.OCamlInterop.Convert

module Account = LibBackend.Account
module Stats = LibBackend.Stats
module Traces = LibBackend.Traces
module Auth = LibBackend.Authorization
module Canvas = LibBackend.Canvas
module Config = LibBackend.Config
module RT = LibExecution.RuntimeTypes
module SA = LibBackend.StaticAssets
module Session = LibBackend.Session
module TFA = LibBackend.TraceFunctionArguments
module TFR = LibBackend.TraceFunctionResults
module TI = LibBackend.TraceInputs

// type add_op_rpc_params =
//   { ops : oplist
//   ; opCtr : int
//         (* option means that we can still deserialize if this field is null, as
//          * doc'd at https://github.com/ocaml-ppx/ppx_deriving_yojson *)
//   ; clientOpCtrId : string option }
//
// type db_stats_rpc_params = {tlids : tlid list}
//
// type upload_function_rpc_params = {fn : RuntimeT.user_fn}
//
// type trigger_handler_rpc_params =
//   { tlid : tlid
//   ; trace_id : RuntimeT.uuid
//   ; input : input_vars }
//
// type route_params =
//   { space : string
//   ; path : string
//   ; modifier : string }
//
// type worker_schedule_update_rpc_params =
//   { name : string
//   ; schedule : string }
//
// type insert_secret_params = RuntimeT.secret
//
// type secrets_list_results = {secrets : RuntimeT.secret list}
//
//
// let causes_any_changes (ps : add_op_rpc_params) : bool =
//   List.exists ~f:Op.has_effect ps.ops
//
// ------------------
//  Functions
// ------------------

// FSCLEANUP
// These types are to match the existing OCaml serializations that the frontend
// can read
type ParamMetadata =
  { name : string
    tipe : string
    block_args : string list
    optional : bool
    description : string }

type PreviewSafety =
  | Safe
  | Unsafe

type FunctionMetadata =
  { name : string
    parameters : ParamMetadata list
    description : string
    return_type : string
    infix : bool
    preview_safety : PreviewSafety
    deprecated : bool
    is_supported_in_query : bool }

let allFunctions = LibExecution.StdLib.StdLib.fns @ LibBackend.StdLib.StdLib.fns

let fsharpOnlyFns : Lazy<Set<string>> =
  lazy
    (LibExecution.StdLib.LibMiddleware.fns
     |> List.map (fun (fn : RT.BuiltInFn) -> (fn.name).ToString())
     |> Set)


let typToApiString (typ : RT.DType) : string =
  match typ with
  | RT.TVariable _ -> "Any"
  | RT.TInt -> "Int"
  | RT.TFloat -> "Float"
  | RT.TBool -> "Bool"
  | RT.TNull -> "Nothing"
  | RT.TChar -> "Character"
  | RT.TStr -> "Str"
  | RT.TList _ -> "List"
  | RT.TRecord _
  | RT.TDict _ -> "Dict"
  | RT.TFn _ -> "Block"
  | RT.TIncomplete -> "Incomplete"
  | RT.TError -> "Error"
  | RT.THttpResponse _ -> "Response"
  | RT.TDB _ -> "Datastore"
  | RT.TDate -> "Date"
  // | TDbList tipe ->
  //     "[" ^ tipe_to_string tipe ^ "]"
  | RT.TPassword -> "Password"
  | RT.TUuid -> "UUID"
  | RT.TOption _ -> "Option"
  | RT.TErrorRail -> "ErrorRail"
  | RT.TResult _ -> "Result"
  | RT.TUserType (name, _) -> name
  | RT.TBytes -> "Bytes"
// | TDeprecated1
// | TDeprecated2
// | TDeprecated3
// | TDeprecated4 _
// | TDeprecated5 _
// | TDeprecated6 ->
// Exception.internal "Deprecated type"

let convertFn (fn : RT.BuiltInFn) : FunctionMetadata =
  { name =
      // CLEANUP: this is difficult to change in OCaml, but is trivial in F# (we
      // should just be able to remove this line with no other change)
      let n = fn.name.ToString()
      if n = "DB::add" then "DB::add_v0" else n
    parameters =
      List.map
        (fun (p : RT.Param) ->
          ({ name = p.name
             tipe = typToApiString p.typ
             block_args = p.blockArgs
             optional = false
             description = p.description } : ParamMetadata))
        fn.parameters
    description = fn.description
    return_type = typToApiString fn.returnType
    preview_safety = if fn.previewable = RT.Pure then Safe else Unsafe
    infix = LibExecution.StdLib.StdLib.isInfixName fn.name
    deprecated = fn.deprecated <> RT.NotDeprecated
    is_supported_in_query = fn.sqlSpec.isQueryable () }


let functionsToString (fns : RT.BuiltInFn list) : string =
  fns
  |> List.filter
       (fun fn -> not (Set.contains (toString fn.name) (fsharpOnlyFns.Force())))
  |> List.map convertFn
  |> List.sortBy (fun fn -> fn.name)
  |> Json.Vanilla.prettySerialize

let adminFunctions : Lazy<string> = lazy (allFunctions |> functionsToString)

let nonAdminFunctions : Lazy<string> =
  lazy
    (allFunctions
     |> List.filter
          (function
          | { name = { module_ = "DarkInternal" } } -> false
          | _ -> true)
     |> functionsToString)


let functions (includeAdminFns : bool) : Lazy<string> =
  if includeAdminFns then adminFunctions else nonAdminFunctions

// --------------------
// Endpoints
// --------------------

module Secrets =
  type ApiSecret = { secret_name : string; secret_value : string }

module Packages =
  type T = List<OT.PackageManager.fn>

  let packages (ctx : HttpContext) : Task<T> =
    task {
      let t = Middleware.startTimer ctx
      let! fns = LibBackend.PackageManager.cachedForAPI.Force()
      t "loadFunctions"
      let result = fns |> List.map Convert.pt2ocamlPackageManagerFn
      t "convertFunctions"
      return result
    }

module Worker =
  type Params = { tlid : tlid }
  type T = { count : int }

  let getStats (ctx : HttpContext) : Task<T> =
    task {
      let t = Middleware.startTimer ctx
      let canvasInfo = Middleware.loadCanvasInfo ctx
      let! args = ctx.BindModelAsync<Params>()
      t "read-api"

      let! result = Stats.workerStats canvasInfo.id args.tlid
      t "analyse-worker-stats"

      return { count = result }
    }


module InitialLoad =
  type ApiUserInfo =
    { username : string // as opposed to UserName.T
      name : string
      admin : bool
      email : string
      id : UserID }

  type ApiStaticDeploy =
    { deploy_hash : string
      url : string
      last_update : System.DateTime
      status : SA.DeployStatus }

  let toApiStaticDeploys (d : SA.StaticDeploy) : ApiStaticDeploy =
    { deploy_hash = d.deployHash
      url = d.url
      last_update = d.lastUpdate
      status = d.status }

  type T =
    { toplevels : ORT.toplevels
      deleted_toplevels : ORT.toplevels
      user_functions : ORT.user_fn<ORT.fluidExpr> list
      deleted_user_functions : ORT.user_fn<ORT.fluidExpr> list
      unlocked_dbs : tlid list
      user_tipes : ORT.user_tipe list
      deleted_user_tipes : ORT.user_tipe list
      assets : List<ApiStaticDeploy>
      op_ctrs : (System.Guid * int) list
      canvas_list : string list
      org_canvas_list : string list
      permission : Auth.Permission option
      orgs : string list
      account : ApiUserInfo
      creation_date : System.DateTime
      worker_schedules : LibBackend.EventQueue.WorkerStates.T
      secrets : List<Secrets.ApiSecret> }

  let initialLoad (ctx : HttpContext) : Task<T> =
    task {
      let t = Middleware.startTimer ctx
      let user = Middleware.loadUserInfo ctx
      let canvasInfo = Middleware.loadCanvasInfo ctx
      let permission = Middleware.loadPermission ctx
      t "loadMiddleware"

      let! canvas = Canvas.loadAll canvasInfo |> Task.map Result.unwrapUnsafe
      t "loadCanvas"

      let! creationDate = Canvas.canvasCreationDate canvasInfo.id
      t "loadCanvasCreationData"


      let! opCtrs =
        Sql.query "SELECT browser_id, ctr FROM op_ctrs WHERE canvas_id = @canvasID"
        |> Sql.parameters [ "canvasID", Sql.uuid canvasInfo.id ]
        |> Sql.executeAsync (fun read -> (read.uuid "browser_id", read.int "ctr"))

      t "loadOpCtrs"

      let! unlocked = LibBackend.UserDB.unlocked canvasInfo.owner canvasInfo.id
      t "getUnlocked"

      let! staticAssets = SA.allDeploysInCanvas canvasInfo.name canvasInfo.id
      t "getStaticAssets"

      let! canvasList = Account.ownedCanvases user.id
      t "getCanvasList"

      let! orgCanvasList = Account.accessibleCanvases user.id
      t "getOrgCanvasList"

      let! orgList = Account.orgs user.id
      t "getOrgList"

      let! workerSchedules = LibBackend.EventQueue.getWorkerSchedules canvas.meta.id
      t "getWorkerSchedules"

      let! secrets = LibBackend.Secret.getCanvasSecrets canvas.meta.id
      t "getSecrets"

      let ocamlToplevels = canvas |> Canvas.toplevels |> Convert.pt2ocamlToplevels

      let ocamlDeletedToplevels =
        canvas |> Canvas.deletedToplevels |> Convert.pt2ocamlToplevels

      let result =
        { toplevels = Tuple3.first ocamlToplevels
          deleted_toplevels = Tuple3.first ocamlDeletedToplevels
          user_functions = Tuple3.second ocamlToplevels
          deleted_user_functions = Tuple3.second ocamlDeletedToplevels
          user_tipes = Tuple3.third ocamlToplevels
          deleted_user_tipes = Tuple3.third ocamlDeletedToplevels
          unlocked_dbs = unlocked
          assets = List.map toApiStaticDeploys staticAssets
          op_ctrs = opCtrs
          canvas_list = List.map toString canvasList
          org_canvas_list = List.map toString orgCanvasList
          permission = permission
          orgs = List.map toString orgList
          worker_schedules = workerSchedules
          account =
            { username = user.username.ToString()
              name = user.name
              email = user.email
              admin = user.admin
              id = user.id }
          creation_date = creationDate
          secrets =
            List.map
              (fun (s : LibBackend.Secret.Secret) ->
                { secret_name = s.name; secret_value = s.value })
              secrets }

      t "buildResultObj"
      return result
    }

module DB =
  module Unlocked =
    type T = { unlocked_dbs : tlid list }

    let getUnlockedDBs (ctx : HttpContext) : Task<T> =
      task {
        let t = Middleware.startTimer ctx
        let canvasInfo = Middleware.loadCanvasInfo ctx
        t "loadCanvasInfo"

        let! unlocked = LibBackend.UserDB.unlocked canvasInfo.owner canvasInfo.id
        t "getUnlocked"
        return { unlocked_dbs = unlocked }
      }

  module Stats =
    type Params = { tlids : tlid list }
    type Stat = { count : int; example : Option<ORT.dval * string> }
    type T = Map<tlid, Stat>

    let getStats (ctx : HttpContext) : Task<T> =
      task {
        let t = Middleware.startTimer ctx
        let canvasInfo = Middleware.loadCanvasInfo ctx
        let! args = ctx.BindModelAsync<Params>()
        t "readApiTLIDs"

        let! c = Canvas.loadAllDBs canvasInfo |> Task.map Result.unwrapUnsafe
        t "loadSavedOps"

        let! result = Stats.dbStats c args.tlids

        // CLEANUP, this is shimming an RT.Dval into an ORT.dval. Nightmare.
        let (result : T) =
          Map.map
            (fun (s : Stats.DBStat) ->
              { count = s.count
                example =
                  Option.map (fun (dv, s) -> (Convert.rt2ocamlDval dv, s)) s.example })
            result

        t "analyse-db-stats"

        return result
      }

module F404 =
  type T = { f404s : List<TI.F404> }

  let get404s (ctx : HttpContext) : Task<T> =
    task {
      let t = Middleware.startTimer ctx
      let canvasInfo = Middleware.loadCanvasInfo ctx
      t "loadCanvasInfo"

      let! f404s = TI.getRecent404s canvasInfo.id
      t "getRecent404s"
      return { f404s = f404s }
    }

module Traces =
  type Params = { tlid : tlid; trace_id : AT.TraceID }

  // CLEANUP: this uses ORT.dval instead of RT.Dval
  type InputVars = List<string * ORT.dval>
  type FunctionArgHash = string
  type HashVersion = int
  type FnName = string
  type FunctionResult = FnName * id * FunctionArgHash * HashVersion * ORT.dval

  type TraceData =
    { input : InputVars
      timestamp : System.DateTime
      function_results : List<FunctionResult> }

  type Trace = AT.TraceID * TraceData
  type TraceResult = { trace : Trace }

  type T = Option<TraceResult>

  type AllTraces = { traces : List<tlid * AT.TraceID> }

  let getTraceData (ctx : HttpContext) : Task<T> =
    task {
      let t = Middleware.startTimer ctx
      let canvasInfo = Middleware.loadCanvasInfo ctx
      t "loadCanvasInfo"

      let! args = ctx.BindModelAsync<Params>()
      t "readBody"

      let! (c : Canvas.T) =
        Canvas.loadTLIDsFromCache canvasInfo [ args.tlid ]
        |> Task.map Result.unwrapUnsafe

      t "loadCanvas"

      // TODO: we dont need the handlers or functions at all here, just for the sample
      // values which we can do on the client instead
      let handler = c.handlers |> Map.get args.tlid

      let! trace =
        match handler with
        | Some h -> Traces.handlerTrace c.meta.id args.trace_id h |> Task.map Some
        | None ->
            match c.userFunctions |> Map.get args.tlid with
            | Some u -> Traces.userfnTrace c.meta.id args.trace_id u |> Task.map Some
            | None -> task { return None }

      // CLEANUP, this is shimming an RT.Dval into an ORT.dval. Nightmare.
      let (trace : Option<Trace>) =
        match trace with
        | Some (id, (traceData : AT.TraceData)) ->
            Some(
              id,
              { input =
                  List.map
                    (fun (s, dv) -> (s, Convert.rt2ocamlDval dv))
                    traceData.input
                timestamp = traceData.timestamp
                function_results =
                  List.map
                    (fun (r1, r2, r3, r4, dv) ->
                      (r1, r2, r3, r4, Convert.rt2ocamlDval dv))
                    traceData.function_results }
            )
        | None -> None

      t "loadTraces"
      return Option.map (fun t -> { trace = t }) trace
    }

  let fetchAllTraces (ctx : HttpContext) : Task<AllTraces> =
    task {
      let t = Middleware.startTimer ctx
      let canvasInfo = Middleware.loadCanvasInfo ctx

      // FSTODO we only need the HTTP handler paths here, so we can remove the loadAll
      // FSTODO don't load traces for deleted handlers
      let! (c : Canvas.T) = Canvas.loadAll canvasInfo |> Task.map Result.unwrapUnsafe
      t "loadCanvas"

      let! hTraces =
        c.handlers
        |> Map.values
        |> List.map
             (fun h ->
               Traces.traceIDsForHandler c h
               |> Task.map (List.map (fun traceid -> (h.tlid, traceid))))
        |> Task.flatten
        |> Task.map List.concat

      t "fetchHandlerTraces"

      let! ufTraces =
        c.userFunctions
        |> Map.values
        |> List.map
             (fun uf ->
               Traces.traceIDsForUserFn c.meta.id uf.tlid
               |> Task.map (List.map (fun traceID -> (uf.tlid, traceID))))
        |> Task.flatten
        |> Task.map List.concat

      t "fetchUserFnTraces"

      return { traces = hTraces @ ufTraces }
    }

module ExecuteFunction =
  let fns =
    lazy
      (LibExecution.StdLib.StdLib.fns @ LibBackend.StdLib.StdLib.fns
       |> Map.fromListBy (fun fn -> RT.FQFnName.Stdlib fn.name))

  module Exe = LibExecution.Execution
  module TraceFunctionArguments = LibBackend.TraceFunctionArguments
  module TraceFunctionResults = LibBackend.TraceFunctionResults
  module DvalRepr = LibExecution.DvalRepr

  type Params =
    { tlid : tlid
      trace_id : AT.TraceID
      caller_id : id
      args : ORT.dval list
      fnname : string }

  type T =
    { result : ORT.dval
      hash : string
      hashVersion : int
      touched_tlids : tlid list
      unlocked_dbs : tlid list }

  let execute (ctx : HttpContext) : Task<T> =
    task {
      let t = Middleware.startTimer ctx
      let canvasInfo = Middleware.loadCanvasInfo ctx
      let! body = ctx.BindModelAsync<Params>()
      t "loadCanvasInfo"

      let! c = Canvas.loadTLIDsWithContext canvasInfo [ body.tlid ]
      let c = Result.unwrapUnsafe c
      t "load-canvas"

      let dbs =
        c.dbs
        |> Map.values
        |> List.map (fun db -> (db.name, PT.DB.toRuntimeType db))
        |> Map.ofList

      let userFns =
        c.userFunctions
        |> Map.values
        |> List.map (fun f -> (f.name, PT.UserFunction.toRuntimeType f))
        |> Map.ofList

      let userTypes =
        c.userTypes
        |> Map.values
        |> List.map (fun t -> ((t.name, t.version), PT.UserType.toRuntimeType t))
        |> Map.ofList

      let secrets =
        (c.secrets |> Map.map (fun pt -> pt.toRuntimeType ()) |> Map.values)

      let args = List.map Convert.ocamlDval2rt body.args
      let! packageFns = LibBackend.PackageManager.cachedForExecution.Force()

      let storeFnResult = TraceFunctionResults.store canvasInfo.id body.trace_id
      let storeFnArguments = TraceFunctionArguments.store canvasInfo.id body.trace_id

      let state =
        Exe.createState
          canvasInfo.owner
          canvasInfo.id
          body.tlid
          (fns.Force())
          packageFns
          dbs
          userFns
          userTypes
          secrets
          Exe.loadNoResults
          storeFnResult
          Exe.loadNoArguments
          storeFnArguments

      t "load-execution-state"

      let! (result, tlids) =
        Exe.executeFunction state body.caller_id args body.fnname

      t "execute-function"

      let! unlocked = LibBackend.UserDB.unlocked canvasInfo.owner canvasInfo.id
      t "get-unlocked"

      let hashVersion = DvalRepr.currentHashVersion
      let hash = DvalRepr.hash hashVersion args

      let result =
        { result = Convert.rt2ocamlDval result
          hash = hash
          hashVersion = hashVersion
          touched_tlids = tlids
          unlocked_dbs = unlocked }

      t "create-result"
      return result
    }


// let execute_function
//     (c : Canvas.canvas) ~execution_id ~tlid ~trace_id ~caller_id ~args fnname =
//   Execution.execute_function
//     ~tlid
//     ~execution_id
//     ~trace_id
//     ~dbs:(TL.dbs c.dbs)
//     ~user_fns:(c.user_functions |> IDMap.data)
//     ~userTypes:(c.userTypes |> IDMap.data)
//     ~package_fns:c.package_fns
//     ~secrets:(Secret.secrets_in_canvas c.id)
//     ~account_id:c.owner
//     ~canvas_id:c.id
//     ~caller_id
//     ~args
//     ~store_fn_arguments:
//       (Stored_function_arguments.store ~canvas_id:c.id ~trace_id)
//     ~store_fn_result:(Stored_function_result.store ~canvas_id:c.id ~trace_id)
//     fnname
//




let endpoints : Endpoint list =
  let h = Middleware.apiHandler
  let oh = Middleware.apiOptionHandler

  [
    // TODO: why is this a POST?
    POST [ routef "/api/%s/packages" (h Packages.packages Auth.Read)
           routef "/api/%s/initial_load" (h InitialLoad.initialLoad Auth.Read)
           routef "/api/%s/get_unlocked_dbs" (h DB.Unlocked.getUnlockedDBs Auth.Read)
           routef "/api/%s/get_db_stats" (h DB.Stats.getStats Auth.Read)
           routef "/api/%s/get_worker_stats" (h Worker.getStats Auth.Read)
           routef "/api/%s/get_404s" (h F404.get404s Auth.Read)
           routef "/api/%s/get_trace_data" (oh Traces.getTraceData Auth.Read)
           routef "/api/%s/all_traces" (h Traces.fetchAllTraces Auth.Read)
           routef "/api/%s/execute_function" (h ExecuteFunction.execute Auth.Read)

           // routef "/api/%s/save_test" (h Testing.saveTest Auth.ReadWrite)
           //    when Config.allow_test_routes ->
           //    save_test_handler ~execution_id parent canvas
           // | `POST, ["api"; canvas; "add_op"] ->
           //     when_can_edit ~canvas (fun _ ->
           //         wrap_editor_api_headers
           //           (admin_add_op_handler ~execution_id ~user parent canvas body))
           // | `POST, ["api"; canvas; "packages"; "upload_function"] when user.admin ->
           //     when_can_edit ~canvas (fun _ ->
           //         wrap_editor_api_headers
           //           (upload_function ~execution_id ~user parent body))
           // | `POST, ["api"; canvas; "trigger_handler"] ->
           //     when_can_edit ~canvas (fun _ ->
           //         wrap_editor_api_headers
           //           (trigger_handler ~execution_id parent canvas body))
           // | `POST, ["api"; canvas; "worker_schedule"] ->
           //     when_can_edit ~canvas (fun _ ->
           //         wrap_editor_api_headers
           //           (worker_schedule ~execution_id parent canvas body))
           // | `POST, ["api"; canvas; "delete_404"] ->
           //     when_can_edit ~canvas (fun _ ->
           //         wrap_editor_api_headers (delete_404 ~execution_id parent canvas body))
           // | `POST, ["api"; canvas; "static_assets"] ->
           //     when_can_edit ~canvas (fun _ ->
           //         wrap_editor_api_headers
           //           (static_assets_upload_handler
           //              ~execution_id
           //              ~user
           //              parent
           //              canvas
           //              req
           //              body))
           // | `POST, ["api"; canvas; "insert_secret"] ->
           //     when_can_edit ~canvas (fun _ ->
           //         wrap_editor_api_headers
           //           (insert_secret ~execution_id parent canvas body))
            ] ]


//
// (* --------------------- *)
// (* JSONable response *)
// (* --------------------- *)
//
// (* Response with miscellaneous stuff, and specific responses from tlids *)
//
// type fofs = SE.four_oh_four list
//
// type get_trace_data_rpc_result = {trace : trace}
//
// let to_get_trace_data_rpc_result (c : Canvas.canvas) (trace : trace) : string =
//   {trace}
//   |> get_trace_data_rpc_result_to_yojson
//   |> Yojson.Safe.to_string ~std:true
//
// type new_trace_push = traceid_tlids
//
// type new_404_push = SE.four_oh_four
//
// (* Toplevel deletion:
//  * The server announces that a toplevel is deleted by it appearing in
//  * deleted_toplevels. The server announces it is no longer deleted by it
//  * appearing in toplevels again. *)
//
// (* A subset of responses to be merged in *)
// type add_op_rpc_result =
//   { toplevels : TL.toplevel list (* replace *)
//   ; deleted_toplevels : TL.toplevel list (* replace, see note above *)
//   ; user_functions : RTT.user_fn list (* replace *)
//   ; deleted_user_functions : RTT.user_fn list
//   ; userTypes : RTT.user_tipe list
//   ; deletedUserTypes : RTT.user_tipe list (* replace, see deleted_toplevels *)
//   }
//
// let empty_to_add_op_rpc_result =
//   { toplevels = []
//   ; deleted_toplevels = []
//   ; user_functions = []
//   ; deleted_user_functions = []
//   ; userTypes = []
//   ; deletedUserTypes = [] }
//
// type add_op_stroller_msg =
//   { result : add_op_rpc_result
//   ; params : Api.add_op_rpc_params }
//
// let to_add_op_rpc_result (c : Canvas.canvas) : add_op_rpc_result =
//   { toplevels = IDMap.data c.dbs @ IDMap.data c.handlers
//   ; deleted_toplevels = IDMap.data c.deleted_handlers @ IDMap.data c.deleted_dbs
//   ; user_functions = IDMap.data c.user_functions
//   ; deleted_user_functions = IDMap.data c.deleted_user_functions
//   ; userTypes = IDMap.data c.userTypes
//   ; deletedUserTypes = IDMap.data c.deletedUserTypes }
//
//
// type all_traces_result = {traces : tlid_traceid list}
//
// let to_all_traces_result (traces : tlid_traceid list) : string =
//   {traces} |> all_traces_result_to_yojson |> Yojson.Safe.to_string ~std:true
//
//
// type get_404s_result = {f404s : fofs}
//
// let to_get_404s_result (f404s : fofs) : string =
//   {f404s} |> get_404s_result_to_yojson |> Yojson.Safe.to_string ~std:true
//
// type time = Time.t
//
// (* Warning: both to_string and date_of_string might raise; we could use _option types instead, but since we are using  this for encoding/decoding typed data, I do not think that is necessary right now *)
// let time_of_yojson (j : Yojson.Safe.t) : time =
//   j
//   (* NOTE: Safe.Util; this is "get a string from a (`String of string)", not "stringify an arbitrary Yojson object" *)
//   |> Yojson.Safe.Util.to_string
//   |> Util.date_of_isostring
//
//
// let time_to_yojson (time : time) : Yojson.Safe.t =
//   time |> Util.isostring_of_date |> fun s -> `String s
//
//
// type trigger_handler_rpc_result = {touched_tlids : tlid list}
//
// let to_trigger_handler_rpc_result touched_tlids : string =
//   {touched_tlids}
//   |> trigger_handler_rpc_result_to_yojson
//   |> Yojson.Safe.to_string ~std:true
//
