open Prelude
open ViewUtils
module B = BlankOr
module TL = Toplevel
module TD = TLID.Dict
module Cmd = Tea.Cmd

type modification = AppTypes.modification
type model = AppTypes.model
type msg = AppTypes.msg
module Mod = AppTypes.Modification

let fontAwesome = Icons.fontAwesome

let tw = Attrs.class
let tw2 = (c1, c2) => Attrs.class(`${c1} ${c2}`)
let tw3 = (c1, c2, c3) => Attrs.class(`${c1} ${c2} ${c3}`)

let missingEventRouteDesc: string = "Undefined"

let delPrefix: string = "deleted-"

type identifier =
  | Tlid(TLID.t)
  | Other(string)

type onClickAction =
  | Destination(AppTypes.Page.t)
  | SendMsg(msg)
  | DoNothing

let tlidOfIdentifier = (identifier): option<TLID.t> =>
  switch identifier {
  | Tlid(tlid) => Some(tlid)
  | Other(_) => None
  }

let entryKeyFromIdentifier = (identifier): string =>
  switch identifier {
  | Tlid(tlid) => "entry-" ++ TLID.toString(tlid)
  | Other(s) => "entry-" ++ s
  }

type rec entry = {
  name: string,
  identifier: identifier,
  onClick: onClickAction,
  uses: option<int>,
  minusButton: option<msg>,
  plusButton: option<msg>,
  // if this is in the deleted section, what does minus do?
  killAction: option<msg>,
  verb: option<string>,
}

and category = {
  count: int,
  name: string,
  plusButton: option<msg>,
  iconAction: option<msg>,
  icon: Html.html<msg>,
  emptyName: string, // if none are present, used in `"No " ++ emptyname` (e.g. "No datastores")
  tooltip: option<AppTypes.Tooltip.source>,
  entries: list<item>,
}

and nestedCategory = {
  count: int,
  name: string,
  icon: Html.html<msg>,
  entries: list<item>,
}

and item =
  | NestedCategory(nestedCategory)
  | Entry(entry)

let rec count = (s: item): int =>
  switch s {
  | Entry(_) => 1
  | NestedCategory(c) => (c.entries |> List.map(~f=count))->List.sum(module(Int))
  }

module Styles = {
  let titleBase = %twc("block text-grey8 tracking-wide font-heading")
}

let iconButton = (~key: string, ~icon: string, ~style: string, handler: msg): Html.html<msg> => {
  let twStyle = %twc("hover:text-sidebar-hover cursor-pointer")
  let event = EventListeners.eventNeither(~key, "click", _ => handler)
  Html.div(list{event, tw2(style, twStyle)}, list{fontAwesome(icon)})
}

let handlerCategory = (
  filter: toplevel => bool,
  name: string,
  emptyName: string,
  action: AppTypes.AutoComplete.omniAction,
  iconAction: option<msg>,
  icon: Html.html<msg>,
  tooltip: AppTypes.Tooltip.source,
  hs: list<PT.Handler.t>,
): category => {
  let handlers = hs |> List.filter(~f=h => filter(TLHandler(h)))
  {
    count: List.length(handlers),
    name: name,
    emptyName: emptyName,
    plusButton: Some(CreateRouteHandler(action)),
    iconAction: iconAction,
    icon: icon,
    tooltip: Some(tooltip),
    entries: List.map(handlers, ~f=h => {
      Entry({
        name: PT.Handler.Spec.name(h.spec) |> B.valueWithDefault(missingEventRouteDesc),
        uses: None,
        identifier: Tlid(h.tlid),
        onClick: Destination(FocusedHandler(h.tlid, None, true)),
        minusButton: None,
        killAction: Some(ToplevelDeleteForever(h.tlid)),
        plusButton: None,
        verb: if TL.isHTTPHandler(TLHandler(h)) {
          h.spec->PT.Handler.Spec.modifier->Option.andThen(~f=B.toOption)
        } else {
          None
        },
      })
    }),
  }
}

let httpCategory = (handlers: list<PT.Handler.t>): category =>
  handlerCategory(
    TL.isHTTPHandler,
    "HTTP",
    "HTTP handlers",
    NewHTTPHandler(None),
    Some(GoToArchitecturalView),
    Icons.darkIcon("http"),
    Http,
    handlers,
  )

let cronCategory = (handlers: list<PT.Handler.t>): category =>
  handlerCategory(
    TL.isCronHandler,
    "Cron",
    "Crons",
    NewCronHandler(None),
    Some(GoToArchitecturalView),
    Icons.darkIcon("cron"),
    Cron,
    handlers,
  )

let replCategory = (handlers: list<PT.Handler.t>): category =>
  handlerCategory(
    TL.isReplHandler,
    "REPL",
    "REPLs",
    NewReplHandler(None),
    Some(GoToArchitecturalView),
    fontAwesome("terminal"),
    Repl,
    handlers,
  )

let workerCategory = (handlers: list<PT.Handler.t>): category => {
  // Show the old workers here for now
  let isWorker = tl => TL.isWorkerHandler(tl) || TL.isDeprecatedCustomHandler(tl)
  handlerCategory(
    isWorker,
    "Worker",
    "Workers",
    NewWorkerHandler(None),
    Some(GoToArchitecturalView),
    fontAwesome("wrench"),
    Worker,
    handlers,
  )
}

let dbCategory = (m: model, dbs: list<PT.DB.t>): category => {
  count: List.length(dbs),
  name: "Datastores",
  emptyName: "Datastores",
  plusButton: Some(CreateDBTable),
  iconAction: Some(GoToArchitecturalView),
  icon: Icons.darkIcon("db"),
  tooltip: Some(Datastore),
  entries: dbs->List.map(~f=db => {
    let uses = db.name == "" ? 0 : Refactor.dbUseCount(m, db.name)
    Entry({
      name: db.name == "" ? "Untitled DB" : db.name,
      identifier: Tlid(db.tlid),
      uses: Some(uses),
      onClick: Destination(FocusedDB(db.tlid, true)),
      minusButton: None,
      killAction: Some(ToplevelDeleteForever(db.tlid)),
      verb: None,
      plusButton: None,
    })
  }),
}

let f404Category = (m: model): category => {
  let f404s = {
    // Generate set of deleted handler specs, stringified
    let deletedHandlerSpecs =
      m.deletedHandlers
      |> Map.values
      |> List.map(~f=(h: PT.Handler.t) => {
        let space = h.spec->PT.Handler.Spec.space->B.toString
        let name = h.spec->PT.Handler.Spec.name->B.toString
        let modifier = h.spec->PT.Handler.Spec.modifier->B.optionToString
        // Note that this concatenated string gets compared to `space ++ path ++ modifier` later.
        // h.spec.name and f404.path are the same thing, with different names. Yes this is confusing.
        space ++ name ++ modifier
      })
      |> Set.String.fromList

    m.f404s
    |> List.uniqueBy(~f=(f: AnalysisTypes.FourOhFour.t) => f.space ++ f.path ++ f.modifier)
    |> // Don't show 404s for deleted handlers
    List.filter(~f=(f: AnalysisTypes.FourOhFour.t) =>
      !Set.member(~value=f.space ++ f.path ++ f.modifier, deletedHandlerSpecs)
    )
  }

  {
    count: List.length(f404s),
    name: "404s",
    emptyName: "404s",
    plusButton: None,
    iconAction: None,
    icon: Icons.darkIcon("fof"),
    tooltip: Some(FourOhFour),
    entries: List.map(f404s, ~f=({space, path, modifier, _} as fof) => Entry({
      name: space == "HTTP" ? path : space ++ " " ++ path,
      uses: None,
      identifier: Other(fof.space ++ fof.path ++ fof.modifier),
      onClick: SendMsg(CreateHandlerFrom404(fof)),
      minusButton: Some(Delete404APICall(fof)),
      killAction: None,
      plusButton: Some(CreateHandlerFrom404(fof)),
      verb: space == "WORKER" ? None : Some(modifier),
    })),
  }
}

let userFunctionCategory = (m: model, ufs: list<PT.UserFunction.t>): category => {
  let fns = ufs |> List.filter(~f=(fn: PT.UserFunction.t) => fn.name != "")
  {
    count: List.length(fns),
    name: "Functions",
    emptyName: "Functions",
    plusButton: Some(CreateFunction),
    iconAction: Some(GoToArchitecturalView),
    icon: Icons.darkIcon("fn"),
    tooltip: Some(Function),
    entries: fns->List.map(~f=fn => {
      Entry({
        name: fn.name,
        identifier: Tlid(fn.tlid),
        uses: Introspect.allUsedIn(fn.tlid, m)->List.length->Some,
        minusButton: None,
        killAction: Some(DeleteUserFunctionForever(fn.tlid)),
        onClick: Destination(FocusedFn(fn.tlid, None)),
        plusButton: None,
        verb: None,
      })
    }),
  }
}

let userTypeCategory = (m: model, types: list<PT.UserType.t>): category => {
  let types = types |> List.filter(~f=(ut: PT.UserType.t) => ut.name != "")
  {
    count: List.length(types),
    name: "Types",
    emptyName: "Types",
    plusButton: Some(CreateType),
    iconAction: None,
    icon: Icons.darkIcon("types"),
    tooltip: None,
    entries: List.map(types, ~f=typ => {
      let minusButton = if Refactor.usedType(m, typ.name) {
        None
      } else {
        Some(Msg.DeleteUserType(typ.tlid))
      }

      Entry({
        name: typ.name,
        identifier: Tlid(typ.tlid),
        uses: Some(Refactor.typeUseCount(m, typ.name)),
        minusButton: minusButton,
        killAction: Some(DeleteUserTypeForever(typ.tlid)),
        onClick: Destination(FocusedType(typ.tlid)),
        plusButton: None,
        verb: None,
      })
    }),
  }
}

let standardCategories = (m, hs, dbs, ufns, types) => {
  let hs = hs |> Map.values |> List.sortBy(~f=tl => TL.sortkey(TLHandler(tl)))
  let dbs = dbs |> Map.values |> List.sortBy(~f=tl => TL.sortkey(TLDB(tl)))
  let ufns = ufns |> Map.values |> List.sortBy(~f=tl => TL.sortkey(TLFunc(tl)))
  let types = types |> Map.values |> List.sortBy(~f=tl => TL.sortkey(TLType(tl)))

  // We want to hide user defined types for users who arent already using them
  // since there is currently no way to use them other than as a function param.
  // we should show user defined types once the user can use them more
  let types = types == list{} ? list{} : list{userTypeCategory(m, types)}

  list{
    httpCategory(hs),
    workerCategory(hs),
    cronCategory(hs),
    replCategory(hs),
    dbCategory(m, dbs),
    userFunctionCategory(m, ufns),
    ...types,
  }
}

let packageManagerCategory = (fns: packageFns): category => {
  let fnNameEntries = (moduleList: list<PT.Package.Fn.t>): list<item> => {
    let fnNames =
      moduleList
      ->List.sortBy(~f=(fn: PT.Package.Fn.t) => fn.name.module_)
      ->List.uniqueBy(~f=(fn: PT.Package.Fn.t) => fn.name.function)

    fnNames->List.map(~f=(fn: PT.Package.Fn.t) => Entry({
      name: fn.name.module_ ++ "::" ++ fn.name.function ++ "_v" ++ string_of_int(fn.name.version),
      identifier: Tlid(fn.tlid),
      onClick: Destination(FocusedPackageManagerFn(fn.tlid)),
      uses: None,
      minusButton: None,
      plusButton: None,
      killAction: None,
      verb: None,
    }))
  }

  let packageEntries = (userList: list<PT.Package.Fn.t>): list<item> => {
    let uniquePackages =
      userList
      ->List.sortBy(~f=(fn: PT.Package.Fn.t) => fn.name.package)
      ->List.uniqueBy(~f=(fn: PT.Package.Fn.t) => fn.name.package)

    uniquePackages->List.map(~f=fn => {
      let packageList = userList->List.filter(~f=f => fn.name.package == f.name.package)

      NestedCategory({
        count: List.length(uniquePackages),
        name: fn.name.package,
        icon: fontAwesome("cubes"),
        entries: fnNameEntries(packageList),
      })
    })
  }

  let uniqueAuthors =
    fns
    ->Map.values
    ->List.sortBy(~f=(fn: PT.Package.Fn.t) => fn.name.owner)
    ->List.uniqueBy(~f=(fn: PT.Package.Fn.t) => fn.name.owner)

  let authorEntries = uniqueAuthors->List.map(~f=(fn: PT.Package.Fn.t) => {
    let authorList = fns->Map.values->List.filter(~f=f => fn.name.owner == f.name.owner)

    NestedCategory({
      count: List.length(uniqueAuthors),
      name: fn.name.owner,
      icon: fontAwesome("user"),
      entries: packageEntries(authorList),
    })
  })

  {
    count: List.length(uniqueAuthors),
    name: "Package Manager",
    emptyName: "Packages",
    plusButton: None,
    iconAction: None,
    icon: fontAwesome("box-open"),
    tooltip: Some(PackageManager),
    entries: authorEntries,
  }
}

let deletedCategory = (m: model): category => {
  let cats = standardCategories(
    m,
    m.deletedHandlers,
    m.deletedDBs,
    m.deletedUserFunctions,
    m.deleteduserTypes,
  )->List.map(~f=(c: category): nestedCategory => {
    name: c.name,
    count: c.count,
    icon: c.icon,
    entries: c.entries->List.map(~f=item =>
      switch item {
      | Entry(e) =>
        let actionOpt =
          e.identifier->tlidOfIdentifier->Option.map(~f=tlid => Msg.RestoreToplevel(tlid))

        Entry({
          ...e,
          plusButton: actionOpt,
          uses: None,
          minusButton: e.killAction,
          onClick: actionOpt->Option.map(~f=msg => SendMsg(msg))->Option.unwrap(~default=DoNothing),
        })
      | NestedCategory(_) => item
      }
    ),
  })

  {
    count: cats->List.map(~f=c => count(NestedCategory(c)))->List.sum(module(Int)),
    name: "Deleted",
    emptyName: "deleted toplevels",
    plusButton: None,
    iconAction: None,
    icon: Icons.darkIcon("deleted"),
    tooltip: Some(Deleted),
    entries: cats->List.map(~f=c => NestedCategory(c)),
  }
}

// ---------------
// Render the nested categories
// ---------------

let viewEntry = (m: model, e: entry): Html.html<msg> => {
  let isSelected = tlidOfIdentifier(e.identifier) == CursorState.tlidOf(m.cursorState)

  let pluslink = switch e.plusButton {
  | Some(msg) if m.permission == Some(ReadWrite) =>
    iconButton(
      ~key=e.name ++ "-plus",
      ~icon="plus-circle",
      ~style=%twc(
        "ml-1.5 group-hover/sidebar-addbutton:text-sidebar-hover inline-block text-grey8"
      ),
      msg,
    )
  | Some(_) | None => Vdom.noNode
  }

  let linkItem = {
    // We add pluslink here as it's hard to get into place otherwise
    let verb = switch e.verb {
    | Some(verb) =>
      let verbStyle = switch verb {
      | "GET" => %twc("text-http-get")
      | "POST" => %twc("text-http-post")
      | "PUT" => %twc("text-http-put")
      | "DELETE" => %twc("text-http-delete")
      | "PATCH" => %twc("text-http-patch")
      | "HEAD" => %twc("text-white2") // TODO
      | "OPTIONS" => %twc("text-http-options")
      | _ => %twc("text-white2")
      }
      Html.span(list{tw2(verbStyle, "ml-4")}, list{Html.text(verb), pluslink})
    | _ => pluslink
    }

    let contents = switch e.onClick {
    | Destination(dest) =>
      let selected = {
        // font-black is same as fa-solid, font-medium produces the empty circle
        let dotStyle = if isSelected {
          %twc("inline-block text-sidebar-hover group-hover:text-orange font-black")
        } else {
          %twc("inline-block text-transparent group-hover:text-sidebar-hover font-medium")
        }
        // unclear why both align-middle and mb-[2px] are needed to make the dot center
        let baseStyle = %twc("text-xxs pl-0.25 pr-0.5 align-middle mb-[2px]")
        fontAwesome(~style=`${baseStyle} ${dotStyle}`, "circle")
      }

      let cls = {
        let color = if e.uses == Some(0) {
          %twc("text-sidebar-secondary")
        } else {
          %twc("text-sidebar-primary")
        }
        let default = %twc("flex justify-between cursor-pointer no-underline outline-none")

        `${default} ${color}`
      }

      list{Url.linkFor(dest, cls, list{Html.span(list{}, list{selected, Html.text(e.name)}), verb})}

    | SendMsg(_) =>
      let pointer = m.permission == Some(ReadWrite) ? %twc("cursor-pointer") : ""
      list{
        Html.span(list{tw2(pointer, %twc("flex justify-between"))}, list{Html.text(e.name), verb}),
      }
    | DoNothing => list{Html.text(e.name), verb}
    }

    let action = switch e.onClick {
    | SendMsg(msg) if m.permission == Some(ReadWrite) =>
      EventListeners.eventNeither(~key=e.name ++ "-clicked-msg", "click", _ => msg)
    | SendMsg(_) | DoNothing | Destination(_) => Vdom.noProp
    }

    Html.span(list{tw(%twc("group inline-block group/sidebar-addbutton w-full")), action}, contents)
  }

  // This prevents the delete button appearing
  // We'll add it back in for 404s specifically at some point
  let minuslink = switch e.minusButton {
  | Some(msg) if m.permission == Some(ReadWrite) =>
    iconButton(
      ~key=entryKeyFromIdentifier(e.identifier),
      ~style=%twc("mr-3 text-sidebar-secondary"),
      ~icon="minus-circle",
      msg,
    )
  | Some(_) | None => Vdom.noNode
  }

  Html.div(list{tw(%twc("mt-1.25 flex"))}, list{minuslink, linkItem})
}

let rec viewItem = (m: model, s: item): Html.html<msg> =>
  switch s {
  | NestedCategory(c) =>
    if c.count > 0 {
      viewNestedCategory(m, c)
    } else {
      Vdom.noNode
    }
  | Entry(e) => viewEntry(m, e)
  }

and viewNestedCategory = (m: model, c: nestedCategory): Html.html<msg> => {
  let title = Html.span(
    list{tw2(Styles.titleBase, %twc("text-base text-left font-bold"))},
    list{Html.text(c.name)},
  )
  let entries = List.map(~f=viewItem(m), c.entries)

  Html.div(list{tw(%twc("mb-4 ml-4"))}, list{title, Html.div(list{tw(%twc("pl-2"))}, entries)})
}

let viewToplevelCategory = (
  m: model,
  name: string,
  emptyName: string,
  plusButton: option<msg>,
  icon: Html.html<msg>,
  iconAction: option<msg>,
  // it's not always obvious whether contents is empty so be explicit (eg
  // Vdom.noNode or empty divs)
  isEmpty: bool,
  contents: list<Html.html<msg>>,
): Html.html<msg> => {
  let sidebarIcon = {
    let plusButton = switch plusButton {
    | Some(msg) if m.permission == Some(ReadWrite) =>
      iconButton(
        ~key="plus-" ++ name,
        ~icon="plus-circle",
        ~style=%twc("text-xs text-grey8 absolute right-0.5 top-4"),
        msg,
      )
    | Some(_) | None => Vdom.noNode
    }

    let icon = {
      let prop = switch iconAction {
      | Some(ev) => EventListeners.eventNeither(~key="click" ++ name, "click", _ => ev)
      | None => Vdom.noProp
      }

      let style = %twc(
        "text-grey5 duration-200 text-2xl group-hover/sidebar-category:text-3xl pr-1 w-full h-9 text-center box-border"
      )

      Html.div(
        list{tw(style), Attrs.title(name), Attrs.role("img"), Attrs.alt(name), prop},
        list{icon},
      )
    }
    Html.div(list{tw(%twc("m-0 relative"))}, list{icon, plusButton})
  }

  let contents = if !isEmpty {
    contents
  } else {
    // margin to make up for the space taken by the invisible dot in others
    list{
      Html.div(list{tw(%twc("ml-3 text-sidebar-secondary"))}, list{Html.text("No " ++ emptyName)}),
    }
  }

  Html.div(
    list{tw(%twc("pb-5 text-center relative group/sidebar-category"))},
    list{
      sidebarIcon,
      Html.div(
        list{
          tw(
            %twc(
              "absolute -top-5 left-14 pt-1.5 pb-3 px-2.5 box-border min-w-[20rem] max-w-[40rem] max-h-96 bg-sidebar-bg shadow-[2px_2px_2px_0_var(--black1)] z-[1] overflow-y-scroll w-max text-left hidden group-hover/sidebar-category:block"
            ),
          ),
        },
        list{
          Html.span(
            list{tw2(Styles.titleBase, %twc("pb-1.5 text-lg text-center"))},
            list{Html.text(name)},
          ),
          ...contents,
        },
      ),
    },
  )
}

// ---------------
// Deploys
// ---------------

let viewDeploy = (d: StaticAssets.Deploy.t): Html.html<msg> => {
  let statusString = switch d.status {
  | Deployed => "Deployed"
  | Deploying => "Deploying"
  }

  let copyBtn = Html.div(
    list{
      tw(%twc("text-xs absolute -top-2 -right-1.5 hover:text-sidebar-hover")),
      EventListeners.eventNeither(
        "click",
        ~key="hash-" ++ d.deployHash,
        m => Msg.ClipboardCopyLivevalue("\"" ++ d.deployHash ++ "\"", m.mePos),
      ),
    },
    list{fontAwesome("copy")},
  )

  let statusColor = switch d.status {
  | Deployed => %twc("text-green")
  | Deploying => %twc("text-sidebar-secondary")
  }

  Html.div(
    list{tw(%twc("flex flex-wrap justify-between items-center mt-4"))},
    list{
      Html.div(
        list{
          tw(
            %twc(
              "relative inline-block border border-solid border-sidebar-secondary p-0.5 rounded-sm"
            ),
          ),
        },
        list{
          Html.a(
            list{
              tw(%twc("text-sm text-sidebar-primary hover:text-sidebar-hover no-underline")),
              Attrs.href(d.url),
              Attrs.target("_blank"),
            },
            list{Html.text(d.deployHash)},
          ),
          copyBtn,
        },
      ),
      Html.div(list{tw2(statusColor, %twc("inline-block"))}, list{Html.text(statusString)}),
      Html.div(
        list{tw(%twc("block w-full text-xxs text-right"))},
        list{Html.text(Js.Date.toUTCString(d.lastUpdate))},
      ),
    },
  )
}

let viewDeployStats = (m: model): Html.html<msg> => {
  viewToplevelCategory(
    m,
    "Static Assets",
    "Static Assets",
    None,
    fontAwesome("file"),
    None,
    m.staticDeploys == list{},
    m.staticDeploys->List.map(~f=viewDeploy),
  )
}

// ---------------
// Secrets
// ---------------

let viewSecret = (s: SecretTypes.t): Html.html<msg> => {
  let copyBtn = Html.div(
    list{
      tw(%twc("text-base hover:text-sidebar-hover")),
      EventListeners.eventNeither(
        "click",
        ~key="copy-secret-" ++ s.secretName,
        m => Msg.ClipboardCopyLivevalue(s.secretName, m.mePos),
      ),
      Attrs.title("Click to copy secret name"),
    },
    list{fontAwesome("copy")},
  )

  let secretValue = Util.obscureString(s.secretValue)
  let secretValue = {
    // If str length > 16 chars, we just want to keep the last 16 chars
    let len = String.length(secretValue)
    let count = len - 16
    if count > 0 {
      String.dropLeft(~count, secretValue)
    } else {
      secretValue
    }
  }

  let style = %twc("text-xs no-underline box-border px-1.5 py-0 overflow-hidden")

  Html.div(
    list{
      tw("flex relative justify-between items-center flex-row flex-nowrap w-80 ml-1 mr-1 mb-2.5"),
    },
    list{
      Html.div(
        list{
          tw(
            %twc(
              "group border border-solid border-sidebar-secondary pt-1 rounded-sm text-sidebar-primary w-72 hover:cursor-pointer hover:text-sidebar-hover hover:border-sidebar-hover"
            ),
          ),
        },
        list{
          Html.span(
            list{tw2(style, %twc("inline-block group-hover:hidden"))},
            list{Html.text(s.secretName)},
          ),
          Html.span(
            list{tw2(style, %twc("hidden group-hover:inline-block"))},
            list{Html.text(secretValue)},
          ),
        },
      ),
      copyBtn,
    },
  )
}

let viewSecretKeys = (m: model): Html.html<AppTypes.msg> =>
  viewToplevelCategory(
    m,
    "Secret Keys",
    "Secret Keys",
    Some(SecretMsg(OpenCreateModal)),
    fontAwesome("user-secret"),
    None,
    m.secrets->List.isEmpty,
    m.secrets->List.map(~f=viewSecret),
  )

// --------------------
// Admin
// --------------------

let adminDebuggerView = (m: model): Html.html<msg> => {
  let stateInfoTohtml = (key: string, value: Html.html<msg>): Html.html<msg> =>
    Html.div(
      list{},
      list{
        Html.text(key),
        Html.text(": "),
        Html.span(list{tw(%twc("max-w-[210px] whitespace-nowrap"))}, list{value}),
      },
    )

  let pageToString = pg =>
    switch pg {
    | AppTypes.Page.Architecture => "Architecture"
    | FocusedPackageManagerFn(tlid) =>
      Printf.sprintf("Package Manager Fn (TLID %s)", TLID.toString(tlid))
    | FocusedFn(tlid, _) => Printf.sprintf("Fn (TLID %s)", TLID.toString(tlid))
    | FocusedHandler(tlid, _, _) => Printf.sprintf("Handler (TLID %s)", TLID.toString(tlid))
    | FocusedDB(tlid, _) => Printf.sprintf("DB (TLID %s)", TLID.toString(tlid))
    | FocusedType(tlid) => Printf.sprintf("Type (TLID %s)", TLID.toString(tlid))
    | SettingsModal(tab) => Printf.sprintf("SettingsModal (tab %s)", Settings.Tab.toText(tab))
    }

  let environment = {
    let colors = switch m.environment {
    | "production" => %twc("text-orange bg-black1")
    | "dev" => %twc("text-blue bg-white1")
    | _ => %twc("text-magenta bg-white1")
    }

    // Outer span is the width of the sidebar and the text is centered within in
    Html.span(
      list{tw(%twc("w-full left-0 top-3.5 leading-none absolute box-border"))},
      list{
        Html.span(
          list{tw2(colors, %twc("max-w-[3.5rem] px-0.25 h-2.5 text-[0.56rem] rounded"))},
          list{Html.text(m.environment)},
        ),
      },
    )
  }

  let stateInfo = Html.div(
    list{},
    list{
      stateInfoTohtml("env", Html.text(m.environment)),
      stateInfoTohtml("page", Html.text(pageToString(m.currentPage))),
      stateInfoTohtml("cursorState", Html.text(AppTypes.CursorState.show(m.cursorState))),
    },
  )

  let input = (
    checked: bool,
    fn: AppTypes.EditorSettings.t => AppTypes.EditorSettings.t,
    label: string,
    style: string,
  ) => {
    let event = EventListeners.eventNoPropagation(
      ~key=`tt-${label}-${checked ? "checked" : "unchecked"}`,
      "mouseup",
      _ => Msg.ToggleEditorSetting(es => fn(es)),
    )

    Html.div(
      list{event, tw2(%twc("pt-0.5"), style)},
      list{
        Html.input(
          list{Attrs.type'("checkbox"), Attrs.checked(checked), tw(%twc("cursor-pointer"))},
          list{},
        ),
        Html.label(list{tw(%twc("ml-2"))}, list{Html.text(label)}),
      },
    )
  }

  let toggleTimer = input(
    m.editorSettings.runTimers,
    es => {...es, runTimers: !es.runTimers},
    "Run Timers",
    "mt-2.5",
  )

  let toggleFluidDebugger = input(
    m.editorSettings.showFluidDebugger,
    es => {...es, showFluidDebugger: !es.showFluidDebugger},
    "Show Fluid Debugger",
    "",
  )

  let toggleHandlerASTs = input(
    m.editorSettings.showHandlerASTs,
    es => {...es, showHandlerASTs: !es.showHandlerASTs},
    "Show Handler ASTs",
    "mb-2.5",
  )

  let debugger = Html.div(
    list{tw(%twc("mb-1.5"))},
    list{
      Html.a(
        list{Attrs.href(ViewScaffold.debuggerLinkLoc(m)), tw(%twc("text-grey8 hover:text-white3"))},
        list{Html.text(m.teaDebuggerEnabled ? "Disable Debugger" : "Enable Debugger")},
      ),
    },
  )

  let saveTestButton = Html.a(
    list{
      EventListeners.eventNoPropagation(~key="stb", "mouseup", _ => Msg.SaveTestButton),
      tw(
        %twc(
          "border border-solid rounded-sm p-1 my-5 h-2.5 w-fit text-xxs text-grey8 cursor-pointer hover:text-black2 hover:bg-grey8 text-left"
        ),
      ),
    },
    list{Html.text("SAVE STATE FOR INTEGRATION TEST")},
  )

  let content = Belt.List.concatMany([
    list{stateInfo, toggleTimer, toggleFluidDebugger, toggleHandlerASTs, debugger, saveTestButton},
  ])

  let icon = Html.div(list{tw(%twc("relative"))}, list{fontAwesome("cog"), environment})

  viewToplevelCategory(m, "Admin", "", None, icon, None, false, content)
}

// --------------------
// Standard view apparatus
// --------------------

let viewSidebar_ = (m: model): Html.html<msg> => {
  let cats = Belt.List.concat(
    standardCategories(m, m.handlers, m.dbs, m.userFunctions, m.userTypes),
    list{f404Category(m), deletedCategory(m), packageManagerCategory(m.functions.packageFunctions)},
  )

  let showAdminDebugger = if m.settings.contributingSettings.general.showSidebarDebuggerPanel {
    adminDebuggerView(m)
  } else {
    Vdom.noNode
  }

  let categories = Belt.List.concat(
    cats->List.map(~f=c =>
      viewToplevelCategory(
        m,
        c.name,
        c.emptyName,
        c.plusButton,
        c.icon,
        c.iconAction,
        c.entries->List.map(~f=count)->List.sum(module(Int)) == 0,
        c.entries->List.map(~f=viewItem(m)),
      )
    ),
    list{viewSecretKeys(m), viewDeployStats(m), showAdminDebugger},
  )

  Html.div(
    list{
      Attrs.id("sidebar-left"), // keep for z-index
      tw(
        %twc(
          "h-full top-0 left-0 p-0 fixed box-border transition-[width] duration-200 bg-sidebar-bg pt-8 w-14"
        ),
      ),
      // Block opening the omnibox here by preventing canvas pan start
      EventListeners.nothingMouseEvent("mousedown"),
      EventListeners.eventNoPropagation(~key="ept", "mouseover", _ => Msg.EnablePanning(false)),
      EventListeners.eventNoPropagation(~key="epf", "mouseout", _ => Msg.EnablePanning(true)),
    },
    categories,
  )
}

let rtCacheKey = (m: model) =>
  (
    m.handlers |> Map.mapValues(~f=(h: PT.Handler.t) => (h.pos, TL.sortkey(TLHandler(h)))),
    m.dbs |> Map.mapValues(~f=(db: PT.DB.t) => (db.pos, TL.sortkey(TLDB(db)))),
    m.userFunctions |> Map.mapValues(~f=(uf: PT.UserFunction.t) => uf.name),
    m.userTypes |> Map.mapValues(~f=(ut: PT.UserType.t) => ut.name),
    m.f404s,
    m.deletedHandlers |> Map.mapValues(~f=(h: PT.Handler.t) => TL.sortkey(TLHandler(h))),
    m.deletedDBs |> Map.mapValues(~f=(db: PT.DB.t) => (db.pos, TL.sortkey(TLDB(db)))),
    m.deletedUserFunctions |> Map.mapValues(~f=(uf: PT.UserFunction.t) => uf.name),
    m.deleteduserTypes |> Map.mapValues(~f=(ut: PT.UserType.t) => ut.name),
    m.staticDeploys,
    m.unlockedDBs,
    m.usedDBs,
    m.usedFns,
    m.usedTypes,
    CursorState.tlidOf(m.cursorState),
    m.environment,
    m.editorSettings,
    m.permission,
    m.currentPage,
    m.tooltipState.tooltipSource,
    m.secrets,
    m.functions.packageFunctions |> Map.mapValues(~f=(t: PT.Package.Fn.t) => t.name.owner),
    m.settings.contributingSettings.general.showSidebarDebuggerPanel,
  ) |> Option.some

let viewSidebar = m => ViewCache.cache1m(rtCacheKey, viewSidebar_, m)
