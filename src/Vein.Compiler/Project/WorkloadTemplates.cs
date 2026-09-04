namespace Vein.Compiler.Project;

/// A starting file for one of the three things VeinScript ships programs for.
public sealed record WorkloadTemplate(string Key, string Title, string FileName, string Blurb);

// WHY: `new bundle` and `new app` produce a correct file that does nothing, and the distance from there
// to a program that runs is the part a newcomer gets wrong. These are three files that already work —
// each is a whole program you can press ▶ on immediately, and then edit into what you meant.
//
// Each carries a header run line in the same format RunConfig reads, so ▶ is correct on a file made
// this way from the first second, exactly as it is for the shipped samples. A template whose own header
// did not follow the convention would teach the convention wrongly.
public static class WorkloadTemplates
{
    public static IReadOnlyList<WorkloadTemplate> All { get; } = new[]
    {
        new WorkloadTemplate("cli", "CLI app", "main.vein",
            "Reads typed lines and answers them. `@Input` in, `@Print` out."),
        new WorkloadTemplate("chat", "Chat / relay app", "relay.vein",
            "One file launched once per participant; a control process relays between them."),
        new WorkloadTemplate("site", "Small website", "site.vein",
            "Answers `@Request` with markup. Serve it, or preview a route in the IDE.")
    };

    /// `entryPath` is where the file will actually live, relative to the folder you would run from —
    /// `Demo/main.vein` for a scratch project, `Demo/Demo/Demo.vein` for the principal of a solution.
    /// It goes into the header run line, which RunConfig reads to drive the Workbench toolbar, so a
    /// hardcoded path would break the toolbar run button on the very first file someone makes.
    public static string Source(string key, string name, string author, string entryPath) => key switch
    {
        "chat" => Chat(name, author, entryPath),
        "site" => Site(name, author, entryPath),
        _ => Cli(name, author, entryPath)
    };

    private static string Cli(string name, string author, string entryPath) => $$"""
// {{name}} — a CLI app. Type a line and it answers.
//
//   veinc run {{entryPath}}

bundle {{name}} by {{author}} {

    shard Greeter {
        run once {
            emit *Vein.Console.Io.@Print { text: "{{name}} — type something and press Enter." }
        }

        // Every line you type arrives as @Input. Ctrl+Z (Windows) / Ctrl+D (Unix) ends the run.
        hear *Vein.Console.Io.@Input as i {
            emit *Vein.Console.Io.@Print { text: "you said: " + i.text }
        }
    }
}

""";

    private static string Chat(string name, string author, string entryPath) => $$"""
// {{name}} — ONE program, launched once per participant, relaying between them.
//
//   Terminal 1:   VEIN_CONSOLE=Control veinc run {{entryPath}}     ← start this first
//   Terminal 2:   VEIN_CONSOLE=Alpha   veinc run {{entryPath}}
//   Terminal 3:   VEIN_CONSOLE=Beta    veinc run {{entryPath}}
//
// `here()` is this process's own console address, read from the VEIN_CONSOLE environment variable, so
// `match here()` is a ROLE SWITCH across separately launched processes. A console's pipe name is
// machine-global, so they find each other with nothing to configure.
//
// Memory is NOT shared. Each process has its own entities; what crosses is messages.

bundle {{name}} by {{author}} {

    // One worker the control process has heard from. An entity per worker, because a shard `var` holds
    // one value and there is no growable list — so the roster IS which entities exist.
    shape $Worker { addr: string }

    shard Boot {
        run once {
            match here() {
                when #Control { emit *Vein.Console.Io.@Print { text: "CONTROL — waiting for workers." } }
                else {
                    emit *Vein.Console.Io.@Print { text: "WORKER " + here() + " — type a line." }
                    emit *Vein.Console.Io.@Send { to: #Control, text: "reporting in" }
                }
            }
        }
    }

    shard Relay {
        var known: bool

        hear *Vein.Console.Io.@Message as m {
            match here() {
                when #Control {
                    known = false
                    target $Worker as w { if w.Worker.addr == m.from { known = true } }
                    if not known {
                        let e = spawn()
                        attach $Worker to e { addr: m.from }
                    }

                    emit *Vein.Console.Io.@Print { text: m.from + ": " + m.text }

                    // Everyone but the sender. There is no `!=`, so inequality is `not (a == b)`.
                    target $Worker as w {
                        if not (w.Worker.addr == m.from) {
                            emit *Vein.Console.Io.@Send { to: w.Worker.addr, text: m.from + ": " + m.text }
                        }
                    }
                }
                else { emit *Vein.Console.Io.@Print { text: "  <- " + m.text } }
            }
        }
    }

    shard Typing {
        hear *Vein.Console.Io.@Input as i {
            match here() {
                when #Control { emit *Vein.Console.Io.@Print { text: "(control relays; it does not chat)" } }
                else { emit *Vein.Console.Io.@Send { to: #Control, text: i.text } }
            }
        }
    }

    shard Reachability {
        hear *Vein.Console.Io.@Undelivered as u {
            emit *Vein.Console.Io.@Print { text: "(no one is listening as " + u.to + ")" }
            target $Worker as w { if w.Worker.addr == u.to { destroy w } }
        }
    }
}

""";

    private static string Site(string name, string author, string entryPath) => $$"""
// {{name}} — a small website. Routing is `if r.path == "/x"`; there is no route table.
//
//   veinc serve {{entryPath}} --port 8080

bundle {{name}} by {{author}} {

    shard Pages {
        hear *Vein.Web.Http.@Request as r {
            if r.path == "/" {
                emit *Vein.Web.Http.@Response {
                    status: 200,
                    body: "<!doctype html><html><head><meta charset=\"utf-8\"><title>{{name}}</title></head>" +
                          "<body><h1>{{name}}</h1><p>Assembled from events.</p>" +
                          "<a href=\"/about\">about</a></body></html>"
                }
            }

            if r.path == "/about" {
                emit *Vein.Web.Http.@Response {
                    status: 200,
                    body: "<!doctype html><html><head><meta charset=\"utf-8\"><title>About</title></head>" +
                          "<body><h1>About</h1><a href=\"/\">home</a></body></html>"
                }
            }
        }
    }
}

""";
}
