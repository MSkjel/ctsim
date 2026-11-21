using System;
using System.Collections.Generic;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>The simulated runtime's data layer: one per simulated project.</summary>
    public sealed class World
    {
        public Unit Unit { get; }
        public SimClock Clock { get; }
        public TagDb Tags { get; }
        public Users Users { get; }
        public EquipDb Equipment { get; }
        public AlarmDb Alarms { get; }
        public TraceWriter? Trace { get; private set; }
        private readonly List<Action> _unhook = new List<Action>();

        /// <param name="projectDir">a project folder</param>
        /// <param name="root">the compiled project to run when several include it (default:
        /// the tag scope's choice)</param>
        /// <param name="clock">a clock to share, else a new paused one</param>
        /// <param name="start">the simulated wall time at t = 0 (ISO text, Unix seconds, null = now)</param>
        /// <param name="writeDelay">seconds a write without WriteLocal takes to arrive</param>
        public World(
            string projectDir,
            string? root = null,
            SimClock? clock = null,
            object? start = null,
            double writeDelay = 0.25
        )
        {
            Unit = new Unit(projectDir, root);
            Clock = clock ?? new SimClock(start);
            Tags = new TagDb(Unit, Clock, writeDelay);
            Users = new Users(Unit, Clock);
            Equipment = new EquipDb(Unit, Tags);
            Alarms = new AlarmDb(Unit, Tags, Clock, Users, Equipment);
            Tags.Alarms = Alarms;
        }

        public static World ForPage(
            string pagePath,
            string? root = null,
            SimClock? clock = null,
            object? start = null,
            double writeDelay = 0.25
        ) => new World(WinPath.DirName(WinPath.Full(pagePath)), root, clock, start, writeDelay);

        /// <summary>Forget every process-wide cache (tables, tag indexes, scopes): tests, or after
        /// the project files changed within one file-time tick.</summary>
        public static void ClearCaches()
        {
            Dbf.ClearCache();
            TagIndex.ClearCache();
            TagScope.Clear();
        }

        public string Project => Unit.Project;
        public string Root => Unit.Root;

        /// <summary>The display client of this process: windows, pages, animation points,
        /// associations. Made on first use.</summary>
        public global::CicodeSim.World.Display.DisplayState Display =>
            global::CicodeSim.World.Display.DisplayState.Of(this);

        /// <summary>A [section] name parameter of the unit's param.dbf.</summary>
        public string? Param(string section, string name, string? def = null) =>
            Unit.Param(section, name, def);

        // ------------------------------------------------------------------ tracing
        /// <summary>Write every tag change and alarm transition to <paramref name="trace"/>.</summary>
        public TraceWriter AttachTrace(TraceWriter trace)
        {
            DetachTrace();
            Trace = trace;
            Action<TagChange> onTag = ch =>
            {
                string name =
                    ch.Element == "field" || ch.Element.Length == 0 ? ch.Name : ch.Name + "." + ch.Element;
                if (ch.Index != null)
                    name +=
                        "["
                        + ch.Index.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "]";
                trace.TagWrite(name, ch.Value, ch.By, Quality.Name(ch.Quality));
            };
            Action<Transition> onAlarm = tr =>
            {
                if (!string.IsNullOrEmpty(tr.Sub))
                    trace.Alarm(tr.Tag, tr.Event, tr.State, tr.By, tr.T, ("sub", tr.Sub));
                else
                    trace.Alarm(tr.Tag, tr.Event, tr.State, tr.By, tr.T);
            };
            _unhook.Add(Tags.Subscribe(onTag));
            Alarms.Listeners.Add(onAlarm);
            _unhook.Add(() => Alarms.Listeners.Remove(onAlarm));
            return trace;
        }

        public TraceWriter OpenTrace(
            string? path = null,
            string scenario = "",
            System.IO.TextWriter? stream = null
        ) => AttachTrace(new TraceWriter(path, stream, Project, scenario, clock: Clock));

        public void DetachTrace()
        {
            foreach (var off in _unhook)
                off();
            _unhook.Clear();
            Trace = null;
        }

        // ------------------------------------------------------------------ running
        /// <summary>Start the alarm server's scan on the clock.</summary>
        public void Start() => Alarms.Start();

        /// <summary>Advance simulated time; returns the timers fired.</summary>
        public int RunFor(double seconds) => Clock.Advance(seconds);

        public ScenarioResult RunScenario(object scenario, IScenarioEngine? engine = null) =>
            ScenarioRunner.RunScenario(this, scenario, engine, Trace);

        /// <summary>Counts of the unit (reads every table): projects, tags, local variables,
        /// equipment, equipment items, alarms per kind, users, roles.</summary>
        public JsonValue Stats()
        {
            var c = JsonValue.NewObject();
            c.Set("project", JsonValue.Of(Project));
            c.Set("root", JsonValue.Of(Root));
            c.Set("projects", JsonValue.Of(Unit.Projects.Count));
            foreach (var kv in Tags.Counts())
                c.Set(kv.Key, JsonValue.Of(kv.Value));
            c.Set("equipment", JsonValue.Of(Equipment.Count));
            var al = JsonValue.NewObject();
            foreach (var kv in Alarms.Counts())
                al.Set(kv.Key, JsonValue.Of(kv.Value));
            c.Set("alarms", al);
            c.Set("users", JsonValue.Of(Users.UserDefs().Count));
            c.Set("roles", JsonValue.Of(Users.Roles().Count));
            c.Set("categories", JsonValue.Of(Alarms.Categories().Count));
            c.Set("priorities", JsonValue.Of(Alarms.Priorities().Count));
            return c;
        }
    }
}
