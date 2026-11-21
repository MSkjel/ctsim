using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>The simulation's access to DLLs and SQL databases (ctsim/options dll / sql): off
    /// by default (the stubs), ask (a permission question, whitelists) and on; the SQL functions
    /// over a stand-in provider, the DLL functions over real Windows libraries.</summary>
    [Collection("World")]
    public sealed class OutsideAccessTests : BuiltinTestBase
    {
        // ------------------------------------------------------------------ a stand-in database
        private sealed class Store
        {
            public readonly List<string> Ran = new List<string>();
            public readonly List<string> Events = new List<string>();
            public readonly List<string> Params = new List<string>();
            public Func<string, DataTable?> Select = _ => null;
            public int Affected = 1;
            public bool FailOpen;
        }

        private sealed class FakeDb : DbConnection
        {
            public readonly Store Store;
            private ConnectionState _state = ConnectionState.Closed;
            private string _cs;

            public FakeDb(Store store, string cs)
            {
                Store = store;
                _cs = cs;
            }
#pragma warning disable CS8765
            public override string ConnectionString
            {
                get => _cs;
                set => _cs = value;
            }
#pragma warning restore CS8765
            public override string Database => "fake";
            public override string DataSource => "fake";
            public override string ServerVersion => "1";
            public override ConnectionState State => _state;

            public override void ChangeDatabase(string databaseName) { }

            public override void Close()
            {
                if (_state != ConnectionState.Closed)
                    Store.Events.Add("close");
                _state = ConnectionState.Closed;
            }

            public override void Open()
            {
                if (Store.FailOpen)
                    throw new InvalidOperationException("the server does not answer");
                Store.Events.Add("open " + _cs);
                _state = ConnectionState.Open;
            }

            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            {
                Store.Events.Add("begin");
                return new FakeTran(this);
            }

            protected override DbCommand CreateDbCommand() => new FakeCommand(this);
        }

        private sealed class FakeTran : DbTransaction
        {
            private readonly FakeDb _db;

            public FakeTran(FakeDb db)
            {
                _db = db;
            }

            protected override DbConnection DbConnection => _db;
            public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

            public override void Commit() => _db.Store.Events.Add("commit");

            public override void Rollback() => _db.Store.Events.Add("rollback");
        }

        private sealed class FakeParam : DbParameter
        {
            public override DbType DbType { get; set; }
            public override ParameterDirection Direction { get; set; }
            public override bool IsNullable { get; set; }
#pragma warning disable CS8765
            public override string ParameterName { get; set; } = "";
            public override string SourceColumn { get; set; } = "";
            public override object? Value { get; set; }
#pragma warning restore CS8765
            public override int Size { get; set; }
            public override bool SourceColumnNullMapping { get; set; }
            public override DataRowVersion SourceVersion { get; set; }

            public override void ResetDbType() { }
        }

        private sealed class FakeParams : DbParameterCollection
        {
            public readonly List<DbParameter> L = new List<DbParameter>();
            public override int Count => L.Count;
            public override object SyncRoot => L;
            public override bool IsFixedSize => false;
            public override bool IsReadOnly => false;
            public override bool IsSynchronized => false;

            public override int Add(object value)
            {
                L.Add((DbParameter)value);
                return L.Count - 1;
            }

            public override void AddRange(Array values)
            {
                foreach (object v in values)
                    Add(v);
            }

            public override void Clear() => L.Clear();

            public override bool Contains(object value) => L.Contains((DbParameter)value);

            public override bool Contains(string value) => IndexOf(value) >= 0;

            public override void CopyTo(Array array, int index) => ((ICollection)L).CopyTo(array, index);

            public override IEnumerator GetEnumerator() => L.GetEnumerator();

            public override int IndexOf(object value) => L.IndexOf((DbParameter)value);

            public override int IndexOf(string parameterName) =>
                L.FindIndex(p => p.ParameterName == parameterName);

            public override void Insert(int index, object value) => L.Insert(index, (DbParameter)value);

            public override void Remove(object value) => L.Remove((DbParameter)value);

            public override void RemoveAt(int index) => L.RemoveAt(index);

            public override void RemoveAt(string parameterName) => L.RemoveAt(IndexOf(parameterName));

            protected override DbParameter GetParameter(int index) => L[index];

            protected override DbParameter GetParameter(string parameterName) => L[IndexOf(parameterName)];

            protected override void SetParameter(int index, DbParameter value) => L[index] = value;

            protected override void SetParameter(string parameterName, DbParameter value) =>
                L[IndexOf(parameterName)] = value;
        }

        private sealed class FakeCommand : DbCommand
        {
            private readonly FakeDb _db;
            private readonly FakeParams _pars = new FakeParams();

            public FakeCommand(FakeDb db)
            {
                _db = db;
            }
#pragma warning disable CS8765
            public override string CommandText { get; set; } = "";
            protected override DbConnection? DbConnection
            {
                get => _db;
                set { }
            }
            protected override DbTransaction? DbTransaction { get; set; }
#pragma warning restore CS8765
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbParameterCollection DbParameterCollection => _pars;

            public override void Cancel() => _db.Store.Events.Add("cancel");

            public override void Prepare() { }

            protected override DbParameter CreateDbParameter() => new FakeParam();

            private void Note()
            {
                if (_db.State != ConnectionState.Open)
                    throw new InvalidOperationException("the connection is closed");
                _db.Store.Ran.Add(CommandText);
                foreach (DbParameter p in _pars.L)
                    _db.Store.Params.Add(
                        p.ParameterName
                            + "="
                            + Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture)
                            + ":"
                            + p.DbType
                    );
                if (CommandText.Contains("BROKEN"))
                    throw new InvalidOperationException("syntax error near BROKEN");
            }

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                Note();
                return (_db.Store.Select(CommandText) ?? new DataTable()).CreateDataReader();
            }

            public override int ExecuteNonQuery()
            {
                Note();
                return _db.Store.Affected;
            }

            public override object? ExecuteScalar()
            {
                Note();
                var t = _db.Store.Select(CommandText);
                return t == null || t.Rows.Count == 0 ? null : t.Rows[0][0];
            }
        }

        private static DataTable People()
        {
            var t = new DataTable();
            t.Columns.Add("Name", typeof(string)).MaxLength = 20;
            t.Columns.Add("Age", typeof(int));
            t.Columns.Add("Born", typeof(DateTime));
            t.Columns.Add("Note", typeof(string));
            string[] names = { "Ann", "Bob", "Cid", "Dee", "Eve" };
            for (int i = 0; i < names.Length; i++)
                t.Rows.Add(
                    names[i],
                    30 + i,
                    new DateTime(1990 + i, 2, 3, 4, 5, 6),
                    i == 1 ? (object)DBNull.Value : "n" + i
                );
            return t;
        }

        // ------------------------------------------------------------------ a viewer that answers
        private sealed class Asker : IInteractionHost
        {
            public readonly List<Dialog> Shown = new List<Dialog>();
            public readonly List<Dialog> Gone = new List<Dialog>();

            /// <summary>The answer given at once ('once', 'session', 'always', 'deny'); null = later.</summary>
            public string? Answer;

            public void Show(Dialog d)
            {
                Shown.Add(d);
                if (Answer == null)
                    return;
                if (Answer == "deny")
                    d.Cancel();
                else
                    d.Ok(Answer);
            }

            public void Changed(Dialog dialog, int field) { }

            public void Closed(Dialog dialog) => Gone.Add(dialog);

            public void Prompt(int window, string text) { }
        }

        private Sim Plain() => Make(pb => pb.Function("noop").Ret().End());

        private sealed class Ctx
        {
            public Sim S = null!;
            public Registry Reg = null!;
            public CicodeTask Task = null!;
            public Store Db = new Store();
            public Asker Ask = new Asker();

            /// <summary>Call a built-in on the task; a blocking one is waited for.</summary>
            public Value Do(string name, params object[] args)
            {
                var vals = args.Select(a => CicodeVm.ValueOf(a)).ToArray();
                Task.Error = 0;
                Task.Unwinding = false;
                Task.State = TaskState.Run;
                Value r = Reg.Call(Func0Table.Default.Lookup(name)!.Index, Task, ArgList.Of(vals));
                if (!r.IsBlock)
                    return r;
                var w = Task.Wait!;
                var until = DateTime.UtcNow.AddSeconds(20);
                while (w.Poll != null && !w.Poll())
                {
                    Assert.True(DateTime.UtcNow < until, name + " never finished");
                    System.Threading.Thread.Sleep(1);
                }
                Task.Wait = null;
                return w.Result!() ?? Value.Nothing;
            }

            public int Error => Task.Error;
        }

        private Ctx Start(OutsideMode dll = OutsideMode.Off, OutsideMode sql = OutsideMode.Off)
        {
            var s = Plain();
            var c = new Ctx
            {
                S = s,
                Reg = (Registry)s.Run.Vm.Builtins!,
                Task = s.Run.Vm.TaskForCall("noop", Array.Empty<Value>()),
            };
            s.Host.Outside.Dll = dll;
            s.Host.Outside.Sql = sql;
            s.Host.SqlFactory = (provider, cs) => new FakeDb(c.Db, cs);
            s.Host.Interaction = c.Ask;
            c.Db.Select = text =>
                text.IndexOf("people", StringComparison.OrdinalIgnoreCase) >= 0 ? People()
                : text.IndexOf("empty", StringComparison.OrdinalIgnoreCase) >= 0 ? People().Clone()
                : null;
            return c;
        }

        // ------------------------------------------------------------------ off
        [Fact]
        public void OffIsTheDefaultAndEveryCallCountsAsNotSimulated()
        {
            var c = Start();
            Assert.Equal(OutsideMode.Off, new SimHost(null).Outside.Dll);
            Assert.Equal(OutsideMode.Off, new SimHost(null).Outside.Sql);
            int n = 0;
            foreach (var (info, fn) in WorldBuiltins.Declared())
            {
                if (fn.Method.DeclaringType != typeof(Sql) && fn.Method.DeclaringType != typeof(Dll))
                    continue;
                if (info.Name == "DLLCallOEM")
                    continue;
                c.Do(info.Name);
                n++;
                Assert.True(
                    c.S.Run.Vm.NotSimulatedCounts.TryGetValue(info.Name, out int k) && k >= 1,
                    info.Name + " not counted"
                );
            }
            Assert.True(n >= 40, "functions checked: " + n);
            Assert.Empty(c.Db.Events);
            Assert.Empty(c.Ask.Shown);
            Assert.Equal(-1, c.Do("SQLConnect", "DSN=x").I);
            Assert.Equal(307, c.Error);
            Assert.Equal(-1, c.Do("DLLOpen", "kernel32.dll", "lstrlenA", "JC").I);
            Assert.Equal(289, c.Error);
        }

        // ------------------------------------------------------------------ SQL
        [Fact]
        public void AConnectionReadsItsRecordsetRowByRow()
        {
            var c = Start(sql: OutsideMode.On);
            long h = c.Do("SQLConnect", "DSN=Plant;UID=me;PWD=secret").I;
            Assert.Equal(0, h);
            Assert.Equal(0, c.Error);
            Assert.Equal("open DSN=Plant;UID=me;PWD=secret", c.Db.Events[0]);
            Assert.Equal("Open", S(c.Do("SQLInfo", h, 5)));
            Assert.Equal(0, c.Do("SQLExec", h, "SELECT * FROM people").I);
            Assert.Equal("SELECT * FROM people", S(c.Do("SQLInfo", h, 1)));
            Assert.Equal(4, c.Do("SQLNumFields", h).I);
            Assert.Equal(-1, c.Do("SQLRowCount", h).I);
            // before the first SQLNext there is no row
            Assert.Equal("", S(c.Do("SQLGetField", h, "Name")));
            Assert.Equal(294, c.Error);
            var names = new List<string>();
            while (c.Do("SQLNext", h).I == 0)
                names.Add(S(c.Do("SQLGetField", h, "name")) + S(c.Do("SQLGetField", h, "Field2")));
            Assert.Equal(294, c.Error);
            Assert.Equal(new[] { "Ann30", "Bob31", "Cid32", "Dee33", "Eve34" }, names);
            // stepping past the last row is recorded as the manager's last error (the recordset
            // has no handle of its own: -1), not as the connection's
            const string end =
                "SQL Error (14 - End of data for recordset) on Handle (-1). End of data for recordset.";
            Assert.Equal(end, S(c.Do("SQLErrMsg", -1)));
            Assert.Equal("Successful completion", S(c.Do("SQLErrMsg", h)));
            // the timestamp format is the sortable one unless [SQL]DefaultTimestampFormat says otherwise
            Assert.Equal(0, c.Do("SQLExec", h, "SELECT * FROM people").I);
            Assert.Equal(0, c.Do("SQLNext", h).I);
            // nothing clears the last error: it stays after calls that succeed
            Assert.Equal(end, S(c.Do("SQLErrMsg", -1)));
            Assert.Equal("1990-02-03T04:05:06", S(c.Do("SQLGetField", h, "Born")));
            Assert.Equal(0, c.Do("SQLIsNullField", h, "Note").I);
            Assert.Equal(0, c.Do("SQLNext", h).I);
            Assert.Equal(1, c.Do("SQLIsNullField", h, "Note").I);
            Assert.Equal("", S(c.Do("SQLGetField", h, "Note")));
            Assert.Equal("", S(c.Do("SQLGetField", h, "Nope")));
            Assert.Equal(266, c.Error);
            // a row number is for the recordsets of SQLGetRecordset only
            Assert.Equal("", S(c.Do("SQLGetField", h, "Name", 0)));
            Assert.Equal(274, c.Error);
            Assert.Equal(307, c.Do("SQLPrev", h).I);
            Assert.Equal(
                "SQL Error (4 - Database error) on Handle (0). SQLPrev is not supported on connected recordsets.",
                S(c.Do("SQLErrMsg", -1))
            );
            Assert.Equal("Successful completion", S(c.Do("SQLErrMsg", h)));
            // a result without rows: SQLNext has no more rows, and the runtime records data error 18
            Assert.Equal(0, c.Do("SQLExec", h, "SELECT * FROM empty").I);
            Assert.Equal(4, c.Do("SQLNumFields", h).I);
            Assert.Equal(294, c.Do("SQLNext", h).I);
            Assert.Equal(
                "SQL Error (18 - Internal DB data inconsistency) on Handle (0). Merging problem for connected data.",
                S(c.Do("SQLErrMsg", -1))
            );
            Assert.Equal(0, c.Do("SQLEnd", h).I);
            // after SQLEnd the recordset is closed
            Assert.Equal(294, c.Do("SQLNext", h).I);
            Assert.Equal(
                "SQL Error (16 - The recordset is closed) on Handle (-1). The recordset is closed.",
                S(c.Do("SQLErrMsg", -1))
            );
            Assert.Equal(0, c.Do("SQLNumFields", h).I);
            Assert.Equal(0, c.Do("SQLDisconnect", h).I);
            Assert.Contains("close", c.Db.Events);
            Assert.Equal(274, c.Do("SQLExec", h, "SELECT 1").I);
            Assert.Equal(274, c.Error);
            Assert.DoesNotContain("SQLConnect", c.S.Run.Vm.NotSimulatedCounts.Keys);
        }

        [Fact]
        public void ARecordsetOfSqlGetRecordsetIsKeptWhole()
        {
            var c = Start(sql: OutsideMode.On);
            long h = c.Do("SQLConnect", "DSN=Plant").I;
            long r = c.Do("SQLGetRecordset", h, "SELECT * FROM people").I;
            Assert.Equal(1, r);
            Assert.Equal(5, c.Do("SQLRowCount", r).I);
            Assert.Equal(4, c.Do("SQLNumFields", r).I);
            Assert.Equal("Dee", S(c.Do("SQLGetField", r, "Name", 3)));
            Assert.Equal(0, c.Do("SQLNext", r).I);
            Assert.Equal("Ann", S(c.Do("SQLGetField", r, "Name")));
            Assert.Equal(294, c.Do("SQLPrev", r).I);
            Assert.Equal(0, c.Do("SQLNext", r).I);
            Assert.Equal(0, c.Do("SQLNext", r).I);
            Assert.Equal("Bob", S(c.Do("SQLGetField", r, "Name")));
            Assert.Equal(0, c.Do("SQLPrev", r).I);
            Assert.Equal("Ann", S(c.Do("SQLGetField", r, "Name")));
            Assert.Equal("SELECT * FROM people", S(c.Do("SQLInfo", r, 1)));
            Assert.Equal("0", S(c.Do("SQLInfo", r, 6)));
            // an empty result is no recordset
            Assert.Equal(-1, c.Do("SQLGetRecordset", h, "SELECT * FROM nothing").I);
            Assert.Equal(0, c.Error);
            // the recordset outlives its connection
            Assert.Equal(0, c.Do("SQLDisconnect", h).I);
            Assert.Equal("Eve", S(c.Do("SQLGetField", r, "Name", 4)));
            Assert.Equal(0, c.Do("SQLEnd", r).I);
            Assert.Equal(-1, c.Do("SQLRowCount", r).I);
            Assert.Equal(274, c.Error);
        }

        [Fact]
        public void StatementsQueriesParametersAndTransactions()
        {
            var c = Start(sql: OutsideMode.On);
            long h = c.Do("SQLCreate", "DSN=Plant").I;
            Assert.Equal(0, h);
            Assert.Empty(c.Db.Events);
            Assert.Equal("Closed", S(c.Do("SQLInfo", h, 5)));
            Assert.Equal(0, c.Do("SQLOpen", h).I);
            c.Db.Affected = 3;
            Assert.Equal(3, c.Do("SQLCall", h, "UPDATE people SET Age = 1").I);
            Assert.Equal(3, c.Do("SQLNumChange", h).I);
            Assert.Equal("Ann", S(c.Do("SQLGetScalar", h, "SELECT Name FROM people")));
            // SQLSet / SQLAppend: the statement an empty one runs
            Assert.Equal(0, c.Do("SQLSet", h, "DELETE FROM ").I);
            Assert.Equal(0, c.Do("SQLAppend", h, "people").I);
            Assert.Equal(3, c.Do("SQLCall", h, "").I);
            Assert.Equal("DELETE FROM people", c.Db.Ran.Last());
            // a query has its own statement and parameters
            long q = c.Do("SQLQueryCreate", h).I;
            Assert.Equal(1, q);
            Assert.Equal(0, c.Do("SQLSet", q, "INSERT INTO people VALUES (?, ?, ?)").I);
            Assert.Equal(0, c.Do("SQLParamsSetAsString", q, "n", "Fay", 3).I);
            Assert.Equal(0, c.Do("SQLParamsSetAsInt", q, "a", 41).I);
            Assert.Equal(0, c.Do("SQLParamsSetAsReal", q, "w", 1.5).I);
            c.Db.Params.Clear();
            Assert.Equal(3, c.Do("SQLCall", q, "").I);
            Assert.Equal(new[] { "n=Fay:AnsiString", "a=41:Int32", "w=1.5:Double" }, c.Db.Params);
            Assert.Equal("INSERT INTO people VALUES (?, ?, ?)", S(c.Do("SQLInfo", q, 1)));
            Assert.Equal(0, c.Do("SQLParamsClearAll", q).I);
            Assert.Equal(0, c.Do("SQLQueryDispose", q).I);
            Assert.Equal(274, c.Do("SQLQueryDispose", q).I);
            // transactions
            Assert.Equal("1", S(c.Do("SQLInfo", h, 7)));
            Assert.Equal(0, c.Do("SQLBeginTran", h).I);
            Assert.Equal("0", S(c.Do("SQLInfo", h, 7)));
            Assert.Equal(307, c.Do("SQLBeginTran", h).I);
            Assert.Contains("Transaction is already pending", S(c.Do("SQLErrMsg", h)));
            Assert.Equal(0, c.Do("SQLCommit", h).I);
            Assert.Equal(307, c.Do("SQLRollBack", h).I);
            Assert.Equal(0, c.Do("SQLBeginTran", h).I);
            Assert.Equal(0, c.Do("SQLRollBack", h).I);
            Assert.Equal(
                new[] { "begin", "commit", "begin", "rollback" },
                c.Db.Events.Where(e => e != "close" && !e.StartsWith("open")).ToArray()
            );
            // a failing statement: the database's message is SQLErrMsg's
            Assert.Equal(-1, c.Do("SQLCall", h, "BROKEN").I);
            Assert.Equal(307, c.Error);
            Assert.Equal(
                "SQL Error (4 - Database error) on Handle (0). syntax error near BROKEN.",
                S(c.Do("SQLErrMsg", -1))
            );
            // the two styles do not mix; SQLDispose needs a closed connection
            Assert.Equal(278, c.Do("SQLDispose", h).I);
            Assert.Equal(0, c.Do("SQLClose", h).I);
            Assert.Equal("Closed", S(c.Do("SQLInfo", h, 5)));
            Assert.Equal(0, c.Do("SQLDispose", h).I);
            Assert.Equal(274, c.Do("SQLDispose", h).I);
            long old = c.Do("SQLConnect", "DSN=Plant").I;
            Assert.Equal(457, c.Do("SQLClose", old).I);
        }

        [Fact]
        public void ADatabaseThatCannotBeReachedFailsTheConnection()
        {
            var c = Start(sql: OutsideMode.On);
            c.Db.FailOpen = true;
            Assert.Equal(-1, c.Do("SQLConnect", "DSN=Plant").I);
            Assert.Equal(307, c.Error);
            Assert.Contains("the server does not answer", S(c.Do("SQLErrMsg", -1)));
            Assert.Equal(-1, c.Do("SQLConnect", "").I);
            Assert.Equal(274, c.Error);
            // foreground code cannot block
            var fg = c.S.Run.Vm.TaskForCall("noop", Array.Empty<Value>(), foreground: true);
            Value r = c.Reg.Call(
                Func0Table.Default.Lookup("SQLConnect")!.Index,
                fg,
                ArgList.Of(Value.Str("DSN=Plant"))
            );
            Assert.Equal(-1, r.I);
            Assert.Equal(282, fg.Error);
        }

        [Fact]
        public void AskAsksBeforeADatabaseIsConnectedToAndBeforeItIsChanged()
        {
            var c = Start(sql: OutsideMode.Ask);
            // denied: as a database that cannot be reached, nothing is opened
            c.Ask.Answer = "deny";
            Assert.Equal(-1, c.Do("SQLConnect", "DSN=Plant;UID=me;PWD=secret").I);
            Assert.Equal(307, c.Error);
            Assert.Empty(c.Db.Events);
            var d = Assert.Single(c.Ask.Shown);
            Assert.Equal(DialogKind.Permission, d.Kind);
            Assert.Equal("sql", d.Access);
            Assert.Equal("connect", d.Action);
            Assert.Equal("DSN=Plant", d.Target);
            Assert.DoesNotContain("secret", d.Text + d.Target + d.Detail);
            Assert.Single(c.Ask.Gone);
            // once: this connection only
            c.Ask.Answer = "once";
            long h = c.Do("SQLConnect", "DSN=Plant;UID=me;PWD=secret").I;
            Assert.Equal(0, h);
            Assert.Equal(2, c.Ask.Shown.Count);
            // a query is not asked about, a change is
            Assert.Equal(0, c.Do("SQLExec", h, "SELECT * FROM people").I);
            Assert.Equal(2, c.Ask.Shown.Count);
            c.Ask.Answer = "deny";
            Assert.Equal(-1, c.Do("SQLCall", h, "DELETE FROM people").I);
            Assert.Equal(307, c.Error);
            Assert.DoesNotContain("DELETE FROM people", c.Db.Ran);
            Assert.Equal("write", c.Ask.Shown[2].Action);
            Assert.Equal("DELETE FROM people", c.Ask.Shown[2].Detail);
            Assert.Contains("not allowed to change DSN=Plant", S(c.Do("SQLErrMsg", h)));
            // session: the database is trusted until the simulation ends
            c.Ask.Answer = "session";
            Assert.Equal(1, c.Do("SQLCall", h, "DELETE FROM people").I);
            Assert.Equal(1, c.Do("SQLCall", h, "INSERT INTO people VALUES (1)").I);
            Assert.Equal(4, c.Ask.Shown.Count);
            Assert.Empty(c.S.Host.Outside.SqlAllowed());
            // always: the whitelist gets the database, the viewers are told
            int told = 0;
            c.S.Host.Outside.Changed = () => told++;
            c.Ask.Answer = "always";
            long other = c.Do(
                "SQLConnect",
                "Driver={SQL Server};Server=srv1;Database=Hist;Trusted_Connection=yes"
            ).I;
            Assert.True(other > 0);
            Assert.Equal(
                new[] { "Driver={SQL Server}; Server=srv1; Database=Hist" },
                c.S.Host.Outside.SqlAllowed()
            );
            Assert.Equal(1, told);
            Assert.Equal(1, c.Do("SQLCall", other, "DROP TABLE x").I);
            Assert.Equal(5, c.Ask.Shown.Count);
            // nobody to ask (headless): denied
            c.S.Host.Interaction = HeadlessInteraction.Instance;
            Assert.Equal(-1, c.Do("SQLConnect", "DSN=Elsewhere").I);
            Assert.Equal(307, c.Error);
        }

        [Fact]
        public void TheWhitelistAndTheStatementKinds()
        {
            Assert.Equal("DSN=Plant", Sql.Identity("DSN=Plant;UID=a;PWD=b"));
            Assert.Equal(
                "Driver={Microsoft dBase Driver (*.dbf)}; Database=C:\\data",
                Sql.Identity("Driver={Microsoft dBase Driver (*.dbf)};DriverID=277;Dbq=C:\\data")
            );
            Assert.Equal(
                "Provider=SQLOLEDB; Server=s; Database=d",
                Sql.Identity(
                    "SCADA Data Provider=OLEDB;Provider=SQLOLEDB;Data Source=s;Initial Catalog=d;Password=p"
                )
            );
            Assert.Equal("Something=1", Sql.Identity("Something=1;Password=p"));
            Assert.False(Sql.Changes("SELECT * FROM t WHERE a = 'DELETE' -- UPDATE\n"));
            Assert.False(Sql.Changes("  select Replace(a, 'x', 'y') from [update]"));
            Assert.False(Sql.Changes("WITH c AS (SELECT 1) SELECT * FROM c"));
            Assert.True(Sql.Changes("SELECT * INTO backup FROM t"));
            Assert.True(Sql.Changes("SELECT 1; DROP TABLE t"));
            Assert.True(Sql.Changes("insert into t values (1)"));
            Assert.True(Sql.Changes("{call proc(1)}"));
            Assert.True(Sql.Changes("EXEC proc"));
            Assert.True(Sql.Changes("/* SELECT */ UPDATE t SET a = 1"));
            Assert.False(Sql.Changes(""));
            var a = new OutsideAccess { Sql = OutsideMode.Ask, Dll = OutsideMode.Ask };
            Assert.Null(a.SqlConnectDecision("DSN=Plant"));
            Assert.True(a.SetAllowed(false, new[] { "dsn=plant", " " }));
            Assert.False(a.SetAllowed(false, new[] { "DSN=Plant" }));
            Assert.True(a.SqlConnectDecision("DSN=Plant"));
            Assert.True(a.SqlWriteDecision("DSN=Plant"));
            Assert.Null(a.DllDecision(@"C:\x\a.dll"));
            a.Dll = OutsideMode.On;
            Assert.True(a.DllDecision(@"C:\x\a.dll"));
            a.Dll = OutsideMode.Off;
            Assert.False(a.DllDecision(@"C:\x\a.dll"));
        }

        /// <summary>A NULL VARIANT (DLLCallEx of a bad handle) has no value to show: the result of a
        /// ctsim/call is null, its text empty.</summary>
        [Fact]
        public void ANullVariantHasNoValueToShow()
        {
            Assert.True(CicodeSim.Sim.ValueFormat.Json(Value.Variant(null)).IsNull);
            Assert.Equal("", CicodeSim.Sim.ValueFormat.Display(Value.Variant(null)));
            Assert.True(
                CicodeSim.Sim.ValueFormat.Json(Value.Variant(new ValueRef(Value.Variant(null)))).IsNull
            );
            Assert.Equal(7, CicodeSim.Sim.ValueFormat.Json(Value.Variant(new ValueRef(Value.Long(7)))).Long);
        }

        /// <summary>A provider's exception as the runtime's connections describe it.</summary>
        private sealed class OdbcException : DbException
        {
            public OdbcException(string message)
                : base(message) { }

            public List<string> Errors { get; } =
                new List<string> { "ERROR [42S02] no such table", "ERROR [01000] more" };
        }

        [Fact]
        public void TheTextsAndWidthsAreTheRuntimes()
        {
            // a failed statement: the message, a '.', the provider's error list; a read: the message alone
            var ex = new OdbcException("ERROR [42S02] no such table");
            Assert.Equal(
                "ERROR [42S02] no such table.Odbc Info: \nIndex #0: Error: ERROR [42S02] no such table\nIndex #1: Error: ERROR [01000] more\n",
                SqlConn.Describe(ex, false)
            );
            Assert.Equal("ERROR [42S02] no such table", SqlConn.Describe(ex, true));
            Assert.Equal("closed", SqlConn.Describe(new InvalidOperationException("closed"), false));
            // SQLFieldInfo's width: text by the column's size
            // up to [SQL]TextColWidth, the rest by the type
            Assert.Equal(20, Sql.Width(typeof(string), 20, 255));
            Assert.Equal(30, Sql.Width(typeof(string), 40, 30));
            Assert.Equal(255, Sql.Width(typeof(string), -1, 255));
            Assert.Equal(255, Sql.Width(typeof(string), 5000, 255));
            Assert.Equal(
                (11, 6, 22, 13, 26, 39, 6, 1),
                (
                    Sql.Width(typeof(int), -1, 255),
                    Sql.Width(typeof(short), -1, 255),
                    Sql.Width(typeof(double), -1, 255),
                    Sql.Width(typeof(float), -1, 255),
                    Sql.Width(typeof(DateTime), -1, 255),
                    Sql.Width(typeof(decimal), -1, 255),
                    Sql.Width(typeof(bool), -1, 255),
                    Sql.Width(typeof(byte), -1, 255)
                )
            );
            // a library without a folder: the runtime's own folder first, as its LoadLibrary
            string bin = Path.Combine(Tmp.Path, "Bin");
            Directory.CreateDirectory(Path.Combine(bin, "sub"));
            File.WriteAllText(Path.Combine(bin, "Mine.dll"), "");
            File.WriteAllText(Path.Combine(bin, "kernel32.dll"), "");
            Assert.Equal(Path.Combine(bin, "Mine.dll"), Dll.Resolve("mine", bin), ignoreCase: true);
            Assert.Equal(
                Path.Combine(bin, "kernel32.dll"),
                Dll.Resolve("kernel32.dll", bin),
                ignoreCase: true
            );
            Assert.Equal(Path.Combine(bin, "sub", "x.dll"), Dll.Resolve("sub/x.dll", bin), ignoreCase: true);
            Assert.Equal(
                Path.Combine(Environment.SystemDirectory, "kernel32.dll"),
                Dll.Resolve("kernel32", null),
                ignoreCase: true
            );
            Assert.Equal(@"C:\Lib\a.dll", Dll.Resolve(@"C:\Lib\a.dll", bin));
            Assert.Equal("nowhere_at_all.dll", Dll.Resolve("nowhere_at_all", bin));
        }

        // ------------------------------------------------------------------ DLLs
        private static string System32(string name) => Path.Combine(Environment.SystemDirectory, name);

        [Fact]
        public void OnLoadsALibraryAndCallsItsFunctions()
        {
            var c = Start(dll: OutsideMode.On);
            long len = c.Do("DLLOpen", "kernel32.dll", "lstrlenA", "JC").I;
            Assert.Equal(0, len);
            Assert.Equal(0, c.Error);
            Assert.Equal("11", S(c.Do("DLLCall", len, "\"hello world\"")));
            Assert.Equal(11, c.Do("DLLCallEx", len, "hello world").I);
            // a number for a string argument is its text
            Assert.Equal("4", S(c.Do("DLLCall", len, "1234")));
            // the same library and function: the same handle, counted
            Assert.Equal(len, c.Do("DLLOpen", "KERNEL32.DLL", "lstrlenA", "JC").I);
            // C runtime functions (cdecl), doubles in and out
            long pow = c.Do("DLLOpen", System32("msvcrt.dll"), "pow", "BBB").I;
            Assert.Equal(1, pow);
            Assert.Equal("1024.", S(c.Do("DLLCall", pow, "2, 10")));
            Assert.Equal(0.125, c.Do("DLLCallEx", pow, 2.0, -3.0).D);
            // compiled code passes the variable arguments, then their number (none: a placeholder and 0)
            Assert.Equal(8.0, c.Do("DLLCallEx", pow, 2.0, 3.0, 2).D);
            Assert.Equal(3, c.Do("DLLCallEx", len, "abc", 1).I);
            long tick = c.Do("DLLOpen", "kernel32.dll", "GetTickCount", "J").I;
            Assert.True(c.Do("DLLCallEx", tick, 0, 0).I != 0);
            Assert.Equal(0, c.Error);
            Assert.Equal(0, c.Do("DLLClose", tick).I);
            long atoi = c.Do("DLLOpen", "msvcrt", "atoi", "JC").I;
            Assert.Equal("-77", S(c.Do("DLLCall", atoi, "\"-77\"")));
            // DLLCallEx checks its arguments against the type letters
            c.Do("DLLCallEx", len, 5);
            Assert.Equal(274, c.Error);
            c.Do("DLLCallEx", len);
            Assert.Equal(274, c.Error);
            c.Do("DLLCallEx", 99, "x");
            Assert.Equal(269, c.Error);
            Assert.Equal("", S(c.Do("DLLCall", 99, "x")));
            Assert.Equal(0, c.Error);
            // closing: the second handle of lstrlenA keeps it open
            Assert.Equal(0, c.Do("DLLClose", len).I);
            Assert.Equal("2", S(c.Do("DLLCall", len, "\"ab\"")));
            Assert.Equal(0, c.Do("DLLClose", len).I);
            Assert.Equal("", S(c.Do("DLLCall", len, "\"ab\"")));
            Assert.Equal(269, c.Do("DLLClose", len).I);
            // what is not there
            Assert.Equal(-1, c.Do("DLLOpen", "kernel32.dll", "NoSuchFunction", "J").I);
            Assert.Equal(289, c.Error);
            Assert.Equal(-1, c.Do("DLLOpen", @"C:\nowhere\none.dll", "f", "J").I);
            Assert.Equal(289, c.Error);
            Assert.Equal(-1, c.Do("DLLOpen", "kernel32.dll", "lstrlenA", "JX").I);
            Assert.Equal(274, c.Error);
            // the functions the simulator answers itself stay its own
            long pid = c.Do("DLLOpen", "Kernel32.dll", "GetCurrentProcessId", "J").I;
            Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, c.Do("DLLCallEx", pid).I);
            Assert.Contains(c.S.Log, l =>
                l.Contains("loaded") && l.IndexOf("kernel32.dll", StringComparison.OrdinalIgnoreCase) >= 0
            );
            Assert.DoesNotContain("DLLOpen", c.S.Run.Vm.NotSimulatedCounts.Keys);
            c.S.Host.ShutdownOutside();
        }

        [Fact]
        public void AskAsksBeforeALibraryIsLoaded()
        {
            var c = Start(dll: OutsideMode.Ask);
            string kernel = Dll.Resolve("kernel32.dll");
            Assert.True(File.Exists(kernel), kernel);
            c.Ask.Answer = "deny";
            Assert.Equal(-1, c.Do("DLLOpen", "kernel32.dll", "lstrlenA", "JC").I);
            Assert.Equal(289, c.Error);
            var d = Assert.Single(c.Ask.Shown);
            Assert.Equal(DialogKind.Permission, d.Kind);
            Assert.Equal("dll", d.Access);
            Assert.Equal("load", d.Action);
            Assert.Equal(kernel, d.Target, ignoreCase: true);
            Assert.Equal("lstrlenA", d.Detail);
            // once: this open only; another function of the library asks again
            c.Ask.Answer = "once";
            long len = c.Do("DLLOpen", "kernel32.dll", "lstrlenA", "JC").I;
            Assert.True(len >= 0);
            Assert.Equal("3", S(c.Do("DLLCall", len, "\"abc\"")));
            Assert.Equal(2, c.Ask.Shown.Count);
            c.Ask.Answer = "session";
            Assert.True(c.Do("DLLOpen", "kernel32.dll", "GetTickCount", "J").I >= 0);
            Assert.Equal(3, c.Ask.Shown.Count);
            Assert.True(c.Do("DLLOpen", "kernel32.dll", "GetLastError", "J").I >= 0);
            Assert.Equal(3, c.Ask.Shown.Count);
            // always: on the whitelist, by the library's full path
            int told = 0;
            c.S.Host.Outside.Changed = () => told++;
            c.Ask.Answer = "always";
            Assert.True(c.Do("DLLOpen", "msvcrt.dll", "atoi", "JC").I >= 0);
            Assert.Equal(
                new[] { Dll.Resolve("msvcrt.dll") },
                c.S.Host.Outside.DllAllowed(),
                StringComparer.OrdinalIgnoreCase
            );
            Assert.Equal(1, told);
            c.Ask.Answer = "deny";
            Assert.True(c.Do("DLLOpen", "msvcrt.dll", "atol", "JC").I >= 0);
            Assert.Equal(4, c.Ask.Shown.Count);
            c.S.Host.ShutdownOutside();
        }

        [Fact]
        public void PageCodeCannotWaitForTheAnswerSoItCountsFromTheNextCall()
        {
            var c = Start(dll: OutsideMode.Ask);
            c.Ask.Answer = null;
            var fg = c.S.Run.Vm.TaskForCall("noop", Array.Empty<Value>(), foreground: true);
            Value Open() =>
                c.Reg.Call(
                    Func0Table.Default.Lookup("DLLOpen")!.Index,
                    fg,
                    ArgList.Of(Value.Str("kernel32.dll"), Value.Str("lstrlenA"), Value.Str("JC"))
                );
            fg.Error = 0;
            Assert.Equal(-1, Open().I);
            Assert.Equal(289, fg.Error);
            var d = Assert.Single(c.Ask.Shown);
            // still unanswered: refused again, not asked again
            Assert.Equal(-1, Open().I);
            Assert.Single(c.Ask.Shown);
            d.Ok("once");
            fg.Error = 0;
            Assert.True(Open().I >= 0);
            Assert.Equal(0, fg.Error);
            Assert.Single(c.Ask.Gone);
            // the one answer was one call, however often the page asked meanwhile: another
            // function of the library asks again (not a second free load)
            Value OpenOther() =>
                c.Reg.Call(
                    Func0Table.Default.Lookup("DLLOpen")!.Index,
                    fg,
                    ArgList.Of(Value.Str("kernel32.dll"), Value.Str("GetTickCount"), Value.Str("J"))
                );
            fg.Error = 0;
            Assert.Equal(-1, OpenOther().I);
            Assert.Equal(2, c.Ask.Shown.Count);
            // an OK without a word (a scenario's {"kind": "permission", "result": "ok"}) is once,
            // applied by the dialog pump before the page asks again
            c.Ask.Shown[1].Ok("");
            c.S.Host.PumpDialogs();
            Assert.Equal(2, c.Ask.Gone.Count);
            fg.Error = 0;
            Assert.True(OpenOther().I >= 0);
            Assert.Equal(2, c.Ask.Shown.Count);
            c.S.Host.ShutdownOutside();
        }

        /// <summary>A background task's wait for the answer, ended or changed under it: a kind
        /// turned off cancels the question (refused); TaskResume ends the wait with the call
        /// refused and the question up; the answer of a task that ended is still applied.</summary>
        [Fact]
        public void TheWaitForAnAnswerEndedUnderTheTask()
        {
            var c = Start(dll: OutsideMode.Ask);
            c.Ask.Answer = null;
            Value Open(string fn)
            {
                c.Task.Error = 0;
                c.Task.Unwinding = false;
                c.Task.State = TaskState.Run;
                return c.Reg.Call(
                    Func0Table.Default.Lookup("DLLOpen")!.Index,
                    c.Task,
                    ArgList.Of(Value.Str("kernel32.dll"), Value.Str(fn), Value.Str("J"))
                );
            }
            // DLLs turned off while the question is up: cancelled, the call is refused
            Assert.True(Open("GetTickCount").IsBlock);
            var w = c.Task.Wait!;
            Assert.False(w.Poll!());
            c.S.Host.Outside.Dll = OutsideMode.Off;
            c.S.Host.ShutdownDll();
            Assert.True(c.Ask.Shown[0].Answered);
            Assert.True(w.Poll!());
            c.Task.Wait = null;
            Assert.Equal(-1, (w.Result!() ?? Value.Nothing).I);
            Assert.Equal(289, c.Task.Error);
            Assert.Single(c.Ask.Gone);
            // an answer that arrives after the kind was turned off denies too
            c.S.Host.Outside.Dll = OutsideMode.Ask;
            Assert.True(Open("GetLastError").IsBlock);
            w = c.Task.Wait!;
            c.S.Host.Outside.Dll = OutsideMode.Off;
            c.Ask.Shown[1].Ok("always");
            Assert.True(w.Poll!());
            c.Task.Wait = null;
            Assert.Equal(-1, (w.Result!() ?? Value.Nothing).I);
            Assert.Empty(c.S.Host.Outside.DllAllowed());
            // TaskResume: the wait ends before the answer, the call is refused, the question stays
            c.S.Host.Outside.Dll = OutsideMode.Ask;
            Assert.True(Open("GetCurrentThreadId").IsBlock);
            w = c.Task.Wait!;
            c.Task.Wait = null;
            Assert.Equal(-1, (w.Result!() ?? Value.Nothing).I);
            Assert.Equal(289, c.Task.Error);
            var d = c.Ask.Shown[2];
            Assert.False(d.Answered);
            // ... and its answer, given later with no task waiting, is applied by the dialog pump
            d.Ok("always");
            c.S.Host.PumpDialogs();
            Assert.Equal(3, c.Ask.Gone.Count);
            Assert.Equal(
                new[] { Dll.Resolve("kernel32.dll") },
                c.S.Host.Outside.DllAllowed(),
                StringComparer.OrdinalIgnoreCase
            );
            c.S.Host.ShutdownOutside();
        }

        /// <summary>An empty statement with no SQLSet text is error 2 (274), not sent; a quoted
        /// attribute value is the same identity as a bare one.</summary>
        [Fact]
        public void AnEmptyStatementIsRefusedBeforeTheDatabase()
        {
            var c = Start(sql: OutsideMode.On);
            long h = c.Do("SQLConnect", "DSN=Plant").I;
            Assert.Equal(0, h);
            Assert.Equal(274, c.Do("SQLExec", h, "").I);
            Assert.Equal(274, c.Error);
            Assert.Empty(c.Db.Ran);
            Assert.Equal(0, c.Do("SQLSet", h, "SELECT * FROM people").I);
            Assert.Equal(0, c.Do("SQLExec", h, "").I);
            Assert.Single(c.Db.Ran);
            Assert.Equal(
                "Driver={SQL Server}; Server=srv1; Database=Hist",
                Sql.Identity("Driver={SQL Server};Server='srv1';Database=\"Hist\";PWD=x")
            );
            c.S.Host.ShutdownOutside();
        }

        /// <summary>The class DllClassCreate makes in the test below.</summary>
        public sealed class ProxyProbe
        {
            private readonly System.Text.StringBuilder _text = new System.Text.StringBuilder();

            public ProxyProbe() { }

            public ProxyProbe(string start, int scale)
            {
                _text.Append(start);
                Scale = scale;
            }

            public int Scale { get; set; } = 1;
            public double Ratio { get; set; }
            public string Text => _text.ToString();
            public int Length => _text.Length;

            public ProxyProbe Append(string s)
            {
                _text.Append(s);
                return this;
            }

            public int Times(int n) => n * Scale;

            public double Half(double x) => x / 2;

            public DateTime Stamp() => new DateTime(2024, 5, 6);

            public void Fail() => throw new InvalidOperationException("the probe failed");
        }

        [Fact]
        public void ADotNetClassIsMadeAndUsed()
        {
            var c = Start(dll: OutsideMode.On);
            string lib = typeof(OutsideAccessTests).Assembly.Location;
            long h = c.Do("DllClassCreate", lib, "proxyprobe").I;
            Assert.True(h > 0, string.Join("; ", c.S.Log));
            Assert.Equal(1, c.Do("DllClassIsValid", h).I);
            // an object result stays on the library's side: its handle comes back
            long same = c.Do("DllClassCallMethod", h, "Append", "abc").I;
            Assert.True(same > 0 && same != h);
            Assert.Equal(3, c.Do("DllClassGetProperty", h, "Length").I);
            Assert.Equal("abc", S(c.Do("DllClassGetProperty", same, "Text")));
            Assert.Equal(0, c.Do("DllClassSetProperty", h, "Scale", 7).I);
            Assert.Equal(21, c.Do("DllClassCallMethod", h, "times", 3).I);
            // (compiled code: the arguments, then their number; none: a placeholder and 0)
            Assert.Equal(28, c.Do("DllClassCallMethod", h, "Times", 4, 1).I);
            Assert.Equal("abc", S(c.Do("DllClassCallMethod", h, "get_Text", 0, 0)));
            Assert.Equal(0, c.Do("DllClassSetProperty", h, "Ratio", 1.5).I);
            Assert.Equal(1.5, c.Do("DllClassGetProperty", h, "Ratio").D);
            Assert.Equal(2.25, c.Do("DllClassCallMethod", h, "Half", 4.5).D);
            long stamp = c.Do("DllClassCallMethod", h, "Stamp").I;
            Assert.Equal(2024, c.Do("DllClassGetProperty", stamp, "Year").I);
            c.Do("DllClassGetProperty", h, "NoSuchProperty");
            Assert.Equal(274, c.Error);
            c.Do("DllClassCallMethod", h, "NoSuchMethod");
            Assert.Equal(274, c.Error);
            c.Do("DllClassCallMethod", h, "Times", 1, 2);
            Assert.Equal(274, c.Error);
            c.Do("DllClassCallMethod", h, "Fail");
            Assert.Equal(274, c.Error);
            Assert.Contains(c.S.Log, l => l.Contains("the probe failed"));
            // constructor arguments
            long two = c.Do("DllClassCreate", lib, "ProxyProbe", "xy", 4).I;
            Assert.Equal("xy", S(c.Do("DllClassGetProperty", two, "Text")));
            Assert.Equal(8, c.Do("DllClassCallMethod", two, "Times", 2).I);
            Assert.Equal(0, c.Do("DllClassDispose", h).I);
            // the objects it gave are gone with it
            Assert.Equal(0, c.Do("DllClassIsValid", same).I);
            Assert.Equal(0, c.Do("DllClassIsValid", stamp).I);
            Assert.Equal(0, c.Do("DllClassIsValid", h).I);
            Assert.Equal(1, c.Do("DllClassIsValid", two).I);
            Assert.Equal(274, c.Do("DllClassDispose", h).I);
            Assert.Equal(-1, c.Do("DllClassCreate", lib, "NoSuchClassAnywhere").I);
            Assert.Equal(274, c.Error);
            c.S.Host.ShutdownOutside();
        }

        [Fact]
        public void TheLibraryHelperProcessSurvivesALibraryThatEndsIt()
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "ctsim.exe");
            if (!File.Exists(exe))
                return; // (the host is not built beside the tests)
            var lines = new List<string>();
            using (var r = new DllChildRunner(exe, "dllhost") { Log = lines.Add, TimeoutMs = 20000 })
            {
                // the helper may be a 32-bit process: its own system folder has the library
                int len = r.Load("kernel32.dll", "lstrlenA", out string error);
                Assert.True(len >= 0, error);
                Assert.True(
                    r.Call(len, "JC", new[] { DllValue.OfStr("hello") }, out DllValue v, out error),
                    error
                );
                Assert.Equal(5, v.I);
                int exit = r.Load("kernel32.dll", "ExitProcess", out error);
                Assert.True(exit >= 0, error);
                Assert.False(r.Call(exit, "JJ", new[] { DllValue.OfInt(3) }, out _, out error));
                Assert.Contains("helper ended", error);
                // the next call starts a new helper, which loads the function again
                Assert.True(r.Call(len, "JC", new[] { DllValue.OfStr("again") }, out v, out error), error);
                Assert.Equal(5, v.I);
                Assert.Equal(2, lines.Count(l => l.Contains("helper started")));
                Assert.Equal(-1, r.Load(@"C:\nowhere\none.dll", "f", out error));
                Assert.Contains("cannot be loaded", error);
            }
        }
    }
}
