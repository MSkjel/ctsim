using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>The runtime's data error codes, their texts and the Cicode error each
    /// becomes.</summary>
    internal static class SqlErr
    {
        public const int None = 0,
            Software = 1,
            WrongArgument = 2,
            NoMemory = 3,
            Database = 4,
            StillActive = 5,
            NoConnection = 6,
            NoRecordset = 7,
            NoStatement = 8,
            Cancelled = 10,
            TranPending = 11,
            NoTran = 12,
            TooManyConnections = 13,
            EndOfData = 14,
            TooManyRecordsets = 15,
            RecordsetClosed = 16,
            FieldNotFound = 17,
            Inconsistent = 18,
            MixedStyles = 20;

        private static readonly string[] Texts =
        {
            "No error",
            "General software error",
            "Wrong argument",
            "No memory",
            "Database error",
            "Connection is still active",
            "Connection doesn't exist",
            "Recordset doesn't exist",
            "Statement doesn't exist",
            "Recordset is feeded",
            "Operation canceled by user",
            "Transaction is already pending",
            "No active transaction",
            "Too many connections created",
            "End of data for recordset",
            "Size of recordsets exceedes the limit",
            "The recordset is closed",
            "Recordset field not found",
            "Internal DB data inconsistency",
            "The specified field does not exist in the device",
            "The new and old style SQL functions are mixed",
        };

        public static string Describe(int e) => Texts[e < 0 || e >= Texts.Length ? 1 : e];

        public static int ToCicode(int e)
        {
            switch (e)
            {
                case 0:
                    return 0;
                case 1:
                    return 256;
                case 2:
                case 6:
                case 7:
                case 8:
                    return 274;
                case 3:
                    return 272;
                case 4:
                case 11:
                case 12:
                case 18:
                    return 307;
                case 5:
                case 9:
                    return 278;
                case 10:
                    return 299;
                case 13:
                case 15:
                    return 271;
                case 14:
                case 16:
                    return 294;
                case 17:
                case 19:
                    return 266;
                case 20:
                    return 457;
                default:
                    return 256;
            }
        }
    }

    /// <summary>The last error of the data manager or of one object.</summary>
    internal sealed class SqlErrorInfo
    {
        public int Error,
            Handle = -1;
        public string Description = "";

        public void Set(int error, int handle, string description)
        {
            Error = error;
            Handle = handle;
            Description = description ?? "";
        }

        public string Message() =>
            Error == 0
                ? ""
                : "SQL Error ("
                    + Error.ToString(CultureInfo.InvariantCulture)
                    + " - "
                    + SqlErr.Describe(Error)
                    + ") on Handle ("
                    + Handle.ToString(CultureInfo.InvariantCulture)
                    + "). "
                    + Description
                    + ".";
    }

    internal abstract class SqlObject
    {
        public int Handle = -1;
        public readonly SqlErrorInfo Last = new SqlErrorInfo();

        /// <summary>-1 no tracing, 0 the calls, 1 the calls with their values.</summary>
        public int TraceLevel = -1;
    }

    /// <summary>One parameter of a statement: type 0 default, 1 NVarChar, 2 NChar,
    /// 3 VarChar, 4 Char, 5 Integer, 6 Double.</summary>
    internal sealed class SqlParam
    {
        public string Name = "";
        public object Value = "";
        public int Type;
    }

    /// <summary>A query of SQLQueryCreate: a statement and its parameters.</summary>
    internal sealed class SqlQuery : SqlObject
    {
        public int Conn;
        public string Statement = "";
        public readonly List<SqlParam> Params = new List<SqlParam>();
    }

    /// <summary>A recordset of SQLGetRecordset: the whole result, kept after the connection.</summary>
    internal sealed class SqlRecordset : SqlObject
    {
        public int Conn;
        public DataTable? Table;
        public long Cursor = -1;
        public string Statement = "",
            ConnString = "";
    }

    /// <summary>What an operation on the connection's own thread gives back.</summary>
    internal sealed class SqlOut
    {
        public int Error;
        public string Description = "";
        public bool Thrown;

        /// <summary>An exception's message as it is (the reads of the connection's recordset),
        /// not with the provider's error list after it (the connection's own operations).</summary>
        public bool PlainMessages;
        public int RowCount;
        public bool RowCountSet;
        public string Text = "";
        public bool IsNull = true;
        public DataTable? Table;
        public SqlBatch? Batch;
        public bool ClearCache;
        public ConnectionState State;
    }

    /// <summary>Rows of the connection's open reader (the runtime reads three at a time).</summary>
    internal sealed class SqlBatch
    {
        public bool Header;
        public List<string> Names = new List<string>();
        public List<Type?> Types = new List<Type?>();
        public List<int> Sizes = new List<int>();
        public List<object?[]> Rows = new List<object?[]>();

        /// <summary>The reader's HasRows at the read.</summary>
        public bool HasRows;
    }

    /// <summary>A database connection. Touched only from its own queue.</summary>
    internal sealed class SqlConn : SqlObject
    {
        // ---- the simulation thread's
        public string ConnString = "",
            Identity = "",
            Provider = "";
        public bool NewStyle;
        public string DefaultStatement = "",
            LegacySelect = "";
        public int Affected;
        public ConnectionState State = ConnectionState.Closed;
        public bool Disposed;

        // the default (connected) recordset's cache
        public List<string> ColNames = new List<string>();
        public List<Type?> ColTypes = new List<Type?>();
        public List<int> ColSizes = new List<int>();
        public List<object?[]> Rows = new List<object?[]>();
        public long Cursor = -1;

        // ---- the queue's
        private readonly DbConnection _db;
        private readonly string _queryTimeoutAttr,
            _connTimeoutAttr,
            _timeoutKey;
        private readonly int _queryTimeoutIni;
        private DbTransaction? _tran;
        private DbCommand? _cmd;
        private DbDataReader? _reader;
        private readonly List<SqlParam> _params = new List<SqlParam>();
        private volatile bool _cancelling;
        private Task _tail = Task.FromResult(0);
        private const int RowQuant = 3;

        public SqlConn(
            DbConnection db,
            string queryTimeoutAttr,
            string connTimeoutAttr,
            string timeoutKey,
            int queryTimeoutIni
        )
        {
            _db = db;
            _queryTimeoutAttr = queryTimeoutAttr;
            _connTimeoutAttr = connTimeoutAttr;
            _timeoutKey = timeoutKey;
            _queryTimeoutIni = queryTimeoutIni;
        }

        public bool TranPending => _tran != null;

        public bool Connected =>
            State == ConnectionState.Open
            || State == ConnectionState.Executing
            || State == ConnectionState.Fetching;

        public void ClearCache()
        {
            ColNames = new List<string>();
            ColTypes = new List<Type?>();
            ColSizes = new List<int>();
            Rows = new List<object?[]>();
            Cursor = -1;
        }

        // ------------------------------------------------------------------ the queue
        private Task<SqlOut> Run(Func<SqlOut, int> work, bool evenWhenCancelling = false)
        {
            var t = _tail.ContinueWith(
                _ =>
                {
                    var o = new SqlOut();
                    if (_cancelling && !evenWhenCancelling)
                    {
                        o.Error = SqlErr.Cancelled;
                        o.State = SafeState();
                        return o;
                    }
                    try
                    {
                        int e = work(o);
                        if (o.Error == 0)
                            o.Error = e;
                    }
                    catch (Exception ex)
                    {
                        Fail(o, ex);
                    }
                    if (_cancelling && !evenWhenCancelling)
                        o.Error = SqlErr.Cancelled;
                    o.State = SafeState();
                    return o;
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default
            );
            _tail = t;
            return t;
        }

        private ConnectionState SafeState()
        {
            try
            {
                return _db.State;
            }
            catch (Exception)
            {
                return ConnectionState.Closed;
            } // swallow-ok: a disposed connection is closed
        }

        private static void Fail(SqlOut o, Exception ex)
        {
            o.Error = SqlErr.Database;
            o.Thrown = true;
            o.Description = Describe(ex, o.PlainMessages);
        }

        /// <summary>A failed operation's description, as the runtime words it.</summary>
        internal static string Describe(Exception ex, bool plain)
        {
            if (plain || !(ex is DbException))
                return ex.Message;
            var sb = new StringBuilder(ex.Message).Append('.');
            string name = ex.GetType().Name;
            string? label =
                name == "OdbcException" ? "Odbc Info: \n"
                : name == "OleDbException" ? "OleDb Info: \n"
                : name == "SqlException" ? "MsSQL Info: \n"
                : null;
            if (label == null)
                return sb.ToString();
            sb.Append(label);
            try
            {
                if (
                    ex.GetType().GetProperty("Errors")?.GetValue(ex, null)
                    is System.Collections.IEnumerable errors
                )
                {
                    int i = 0;
                    foreach (object e in errors)
                        sb.Append("Index #")
                            .Append(i++.ToString(CultureInfo.InvariantCulture))
                            .Append(": Error: ")
                            .Append(e)
                            .Append("\n");
                }
            }
            catch (Exception) { } // swallow-ok: a provider exception without an error list
            return sb.ToString();
        }

        private void CloseReader(SqlOut o)
        {
            if (_reader != null)
            {
                try
                {
                    _reader.Close();
                    _reader.Dispose();
                }
                catch (Exception) { } // swallow-ok: the reader of a failed or cancelled command
                _reader = null;
            }
            o.ClearCache = true;
        }

        private void SetConnectionTimeout()
        {
            if (_connTimeoutAttr.Length == 0 || !int.TryParse(_connTimeoutAttr, out int n))
                return;
#if NETFRAMEWORK
            if (_db is System.Data.Odbc.OdbcConnection odbc)
            {
                odbc.ConnectionTimeout = n;
                return;
            }
#endif
            var b = new DbConnectionStringBuilder { ConnectionString = _db.ConnectionString };
            b[_timeoutKey.Length > 0 ? _timeoutKey : "Connect Timeout"] = n;
            _db.ConnectionString = b.ToString();
        }

        private DbCommand Command(string statement, List<SqlParam>? pars)
        {
            // (the previous statement's command goes first: its reader is closed by now; an ODBC
            // command holds a statement handle until it is disposed)
            if (_cmd != null)
            {
                try
                {
                    _cmd.Dispose();
                }
                catch (Exception) { } // swallow-ok: a command of a connection that is gone
                _cmd = null;
            }
            var c = _db.CreateCommand();
            _cmd = c;
            c.CommandText = statement;
            c.CommandType = CommandType.Text;
            if (_tran != null)
                c.Transaction = _tran;
            int timeout = _queryTimeoutIni > -1 ? _queryTimeoutIni : 30;
            if (_queryTimeoutAttr.Length > 0 && int.TryParse(_queryTimeoutAttr, out int qa))
                timeout = qa;
            c.CommandTimeout = timeout;
            foreach (var p in pars ?? _params)
            {
                var dp = c.CreateParameter();
                dp.ParameterName = p.Name;
                dp.Value = p.Value;
                switch (p.Type)
                {
                    case 1:
                        dp.DbType = DbType.String;
                        break;
                    case 2:
                        dp.DbType = DbType.StringFixedLength;
                        break;
                    case 3:
                        dp.DbType = DbType.AnsiString;
                        break;
                    case 4:
                        dp.DbType = DbType.AnsiStringFixedLength;
                        break;
                    case 5:
                        dp.DbType = DbType.Int32;
                        break;
                    case 6:
                        dp.DbType = DbType.Double;
                        break;
                }
                c.Parameters.Add(dp);
            }
            return c;
        }

        // the runtime's type for a schema table that gives a DbType
        private static readonly Dictionary<DbType, Type> SchemaTypes = new Dictionary<DbType, Type>
        {
            { DbType.AnsiString, typeof(string) },
            { DbType.Binary, typeof(byte[]) },
            { DbType.Byte, typeof(byte) },
            { DbType.Boolean, typeof(bool) },
            { DbType.Currency, typeof(long) },
            { DbType.Date, typeof(DateTime) },
            { DbType.DateTime, typeof(DateTime) },
            { DbType.Decimal, typeof(decimal) },
            { DbType.Double, typeof(double) },
            { DbType.Guid, typeof(Guid) },
            { DbType.Int16, typeof(short) },
            { DbType.Int32, typeof(int) },
            { DbType.Int64, typeof(long) },
            { DbType.Object, typeof(object) },
            { DbType.SByte, typeof(sbyte) },
            { DbType.Single, typeof(float) },
            { DbType.String, typeof(string) },
            { DbType.Time, typeof(DateTime) },
            { DbType.UInt16, typeof(ushort) },
            { DbType.UInt32, typeof(uint) },
            { DbType.UInt64, typeof(ulong) },
            { DbType.VarNumeric, typeof(decimal) },
            { DbType.AnsiStringFixedLength, typeof(string) },
            { DbType.StringFixedLength, typeof(string) },
            { DbType.Xml, typeof(string) },
            { DbType.DateTime2, typeof(DateTime) },
            { DbType.DateTimeOffset, typeof(DateTimeOffset) },
        };

        /// <summary>Up to three rows of the open reader (the first read, then the next):
        /// nothing when the result has no rows; the data error 14 when it had rows and the first
        /// read finds no more.</summary>
        private int Fetch(SqlBatch b, bool names)
        {
            var r = _reader!;
            for (int i = 0; i < RowQuant; i++)
            {
                b.HasRows = r.HasRows;
                if (!b.HasRows)
                    break;
                if (!r.Read())
                    return i == 0 ? SqlErr.EndOfData : 0;
                int n = r.FieldCount;
                if (i == 0 && names)
                    for (int k = 0; k < n; k++)
                    {
                        b.Names.Add(r.GetName(k));
                        b.Types.Add(r.GetFieldType(k));
                        b.Sizes.Add(-1);
                    }
                var row = new object?[n];
                for (int k = 0; k < n; k++)
                    row[k] = r[k];
                b.Rows.Add(row);
            }
            return 0;
        }

        /// <summary>The header (from the reader's schema table: the name, the .NET type and the
        /// column size of each column) and the first rows. A statement without a result has no
        /// schema table: no columns, nothing read.</summary>
        private int FirstRead(SqlOut o)
        {
            var r = _reader;
            if (r == null || r.IsClosed)
                return SqlErr.RecordsetClosed;
            var b = new SqlBatch { Header = true };
            o.Batch = b;
            DataTable? schema = r.GetSchemaTable();
            if (schema == null)
                return 0;
            foreach (DataRow row in schema.Rows)
            {
                object name = row["ColumnName"],
                    type = row["DataType"],
                    size = row["ColumnSize"];
                Type? net = typeof(object);
                if (type is Type ty)
                    net = ty;
                else if (type is DbType dt && SchemaTypes.TryGetValue(dt, out Type? mapped))
                    net = mapped;
                b.Names.Add(name?.ToString() ?? "");
                b.Types.Add(net);
                b.Sizes.Add(size is int n ? n : -1);
            }
            return Fetch(b, false);
        }

        public Task<SqlOut> Open() =>
            Run(o =>
            {
                CloseReader(o);
                SetConnectionTimeout();
                _db.Open();
                return 0;
            });

        private int CloseNow(SqlOut o)
        {
            CloseReader(o);
            if (_tran != null)
            {
                try
                {
                    _tran.Rollback();
                }
                catch (Exception ex)
                {
                    Fail(o, ex);
                }
            }
            try
            {
                _db.Close();
            }
            catch (Exception ex)
            {
                Fail(o, ex);
            }
            if (_tran != null)
            {
                try
                {
                    _tran.Dispose();
                }
                catch (Exception) { } // swallow-ok: the transaction of a closed connection
                _tran = null;
            }
            if (_cmd != null)
            {
                try
                {
                    _cmd.Dispose();
                }
                catch (Exception) { } // swallow-ok: the command of a closed connection
                _cmd = null;
            }
            return o.Error;
        }

        public Task<SqlOut> Close() => Run(CloseNow);

        public Task<SqlOut> CloseAndDispose() =>
            Run(
                o =>
                {
                    int e = CloseNow(o);
                    try
                    {
                        _db.Dispose();
                    }
                    catch (Exception) { } // swallow-ok: disposing a connection that failed to close
                    return e;
                },
                evenWhenCancelling: true
            );

        /// <summary>The simulation ends, or the connection object is disposed: cancel what
        /// runs, close and free (nobody waits for it).</summary>
        public void Shutdown()
        {
            _cancelling = true;
            try
            {
                _cmd?.Cancel();
            }
            catch (Exception) { } // swallow-ok: nothing runs
            CloseAndDispose();
        }

        public Task<SqlOut> Exec(string statement, List<SqlParam>? pars) =>
            Run(o =>
            {
                CloseReader(o);
                o.PlainMessages = true;
                var c = Command(statement, pars);
                _reader = c.ExecuteReader();
                o.RowCount = _reader.RecordsAffected;
                o.RowCountSet = true;
                return FirstRead(o);
            });

        public Task<SqlOut> Next() =>
            Run(o =>
            {
                o.PlainMessages = true;
                var b = new SqlBatch();
                o.Batch = b;
                if (_reader == null || _reader.IsClosed)
                    return SqlErr.RecordsetClosed;
                return Fetch(b, true);
            });

        public Task<SqlOut> End() =>
            Run(o =>
            {
                o.PlainMessages = true;
                if (_reader != null && !_reader.IsClosed)
                    _reader.Close();
                return 0;
            });

        public Task<SqlOut> GetRecordset(string statement, List<SqlParam>? pars) =>
            Run(o =>
            {
                CloseReader(o);
                var c = Command(statement, pars);
                using (var r = c.ExecuteReader())
                {
                    o.PlainMessages = true;
                    o.RowCount = r.RecordsAffected;
                    o.RowCountSet = true;
                    var t = new DataTable();
                    t.Load(r, LoadOption.OverwriteChanges);
                    o.Table = t;
                }
                return 0;
            });

        public Task<SqlOut> NonQuery(string statement, List<SqlParam>? pars) =>
            Run(o =>
            {
                CloseReader(o);
                o.RowCountSet = true;
                var c = Command(statement, pars);
                o.RowCount = c.ExecuteNonQuery();
                return 0;
            });

        public Task<SqlOut> Scalar(
            string statement,
            List<SqlParam>? pars,
            int textWidth,
            string dateFormat
        ) =>
            Run(o =>
            {
                CloseReader(o);
                o.RowCountSet = true;
                var c = Command(statement, pars);
                object? v = c.ExecuteScalar();
                return Sql.Text(v, out o.IsNull, out o.Text, textWidth, dateFormat);
            });

        public Task<SqlOut> BeginTran() =>
            Run(o =>
            {
                CloseReader(o);
                if (_tran != null)
                    return SqlErr.TranPending;
                _tran = _db.BeginTransaction();
                return 0;
            });

        public Task<SqlOut> EndTran(bool commit) =>
            Run(o =>
            {
                CloseReader(o);
                if (_tran == null)
                    return SqlErr.NoTran;
                try
                {
                    if (commit)
                        _tran.Commit();
                    else
                        _tran.Rollback();
                }
                finally
                {
                    try
                    {
                        _tran.Dispose();
                    }
                    catch (Exception) { } // swallow-ok: a transaction that failed to end
                    _tran = null;
                }
                return 0;
            });

        public Task<SqlOut> AddParam(SqlParam p) =>
            Run(o =>
            {
                _params.Add(p);
                return 0;
            });

        public Task<SqlOut> ClearParams() =>
            Run(o =>
            {
                _params.Clear();
                return 0;
            });

        /// <summary>SQLCancel: what waits in the queue is refused (error 10), the running
        /// command is cancelled.</summary>
        public void Cancel()
        {
            _cancelling = true;
            Run(
                o =>
                {
                    _cancelling = false;
                    return 0;
                },
                evenWhenCancelling: true
            );
            try
            {
                _cmd?.Cancel();
            }
            catch (Exception) { } // swallow-ok: nothing runs, or the provider cannot cancel
        }
    }

    /// <summary>The data manager's objects of one simulation: connections, recordsets and
    /// queries share one handle table.</summary>
    internal sealed class SqlWorld
    {
        public readonly SortedList<int, SqlObject> Objects = new SortedList<int, SqlObject>();
        public readonly SqlErrorInfo Last = new SqlErrorInfo();
        public int LastHandle = -1;
        public int TraceLevel = -1;

        public int Register(SqlObject o)
        {
            int start = LastHandle + 1;
            if (start >= 32767)
                start = 0;
            int h = -1;
            for (int i = start; i < 32767 && h < 0; i++)
                if (!Objects.ContainsKey(i))
                    h = i;
            for (int i = 0; i < start && h < 0; i++)
                if (!Objects.ContainsKey(i))
                    h = i;
            LastHandle = h;
            if (h < 0)
                return -1;
            o.Handle = h;
            o.TraceLevel = TraceLevel;
            Objects[h] = o;
            return h;
        }

        public T? Get<T>(long handle)
            where T : SqlObject =>
            handle >= 0 && handle <= int.MaxValue && Objects.TryGetValue((int)handle, out var o)
                ? o as T
                : null;

        public int Count<T>()
            where T : SqlObject
        {
            int n = 0;
            foreach (var o in Objects.Values)
                if (o is T)
                    n++;
            return n;
        }

        /// <summary>Unregister a connection and its queries.</summary>
        public void RemoveConnection(SqlConn c)
        {
            var gone = new List<int>();
            foreach (var kv in Objects)
                if (kv.Value is SqlQuery q && q.Conn == c.Handle)
                    gone.Add(kv.Key);
            foreach (int k in gone)
                Objects.Remove(k);
            Objects.Remove(c.Handle);
            c.Disposed = true;
        }

        /// <summary>The simulation ends: every connection is cancelled, closed and freed.</summary>
        public void Shutdown()
        {
            foreach (var o in new List<SqlObject>(Objects.Values))
                if (o is SqlConn c)
                {
                    try
                    {
                        c.Shutdown();
                    }
                    catch (Exception) { } // swallow-ok: the simulation is ending
                }
            Objects.Clear();
        }
    }

    public sealed partial class SimHost
    {
        internal readonly SqlWorld SqlData = new SqlWorld();

        /// <summary>Makes the database connection of a provider ('odbc', 'oledb', 'sqlclient')
        /// and a connection string; null = the built-in providers of this build (tests: a stand-in).</summary>
        internal Func<string, string, DbConnection?>? SqlFactory;

        /// <summary>Close every SQL connection of the simulation (it ends).</summary>
        public void ShutdownSql()
        {
            Outside.CancelAsked("sql"); // (the questions up: refused)
            SqlData.Shutdown();
        }
    }

    /// <summary>The SQL functions.</summary>
    public static class Sql
    {
        // ------------------------------------------------------------------ helpers
        private static bool Off(SimHost h) => h.Outside.Sql == OutsideMode.Off;

        private static Value Stub(SimHost h, CicodeTask t, string name, ArgList a, Value r)
        {
            h.NoteStub(t, name, a, r);
            return r;
        }

        /// <summary>Raise the Cicode error of a data error: <paramref name="quiet"/> = inside the
        /// runtime's ErrSet(1) (with (0, 1): reported as suppressed, never halts).</summary>
        private static int Raise(CicodeTask t, int dataError, bool quiet)
        {
            int code = SqlErr.ToCicode(dataError);
            if (code == 0)
                return 0;
            RaiseCode(t, code, quiet);
            return code;
        }

        private static void RaiseCode(CicodeTask t, int code, bool quiet)
        {
            if (!quiet)
            {
                t.RaiseError(code, false);
                return;
            }
            int level = t.ErrSetLevel;
            t.ErrSetLevel = 1;
            try
            {
                t.RaiseError(code, false);
            }
            finally
            {
                t.ErrSetLevel = level;
            }
        }

        private static void SetError(SimHost h, SqlObject? o, int handle, int error, string description)
        {
            h.SqlData.Last.Set(error, handle, description);
            o?.Last.Set(error, handle, description);
            h.SqlFailed = true;
        }

        /// <summary>No object of the wanted kind has this handle (274).</summary>
        private static void NoHandle(SimHost h, CicodeTask t, long handle, bool quiet)
        {
            SetError(h, null, (int)Math.Max(-1, Math.Min(handle, 32767)), SqlErr.WrongArgument, "");
            RaiseCode(t, Codes.InvalidArg, quiet);
        }

        private static bool Foreground(CicodeTask t)
        {
            if (!t.Foreground)
                return false;
            t.RaiseError(Codes.FgBlock, false);
            return true;
        }

        private static void Trace(SimHost h, CicodeTask t, SqlObject? o, int level, string text)
        {
            int l = o != null ? o.TraceLevel : h.SqlData.TraceLevel;
            if (l >= level)
                h.Note(t, "SQL trace: " + text);
        }

        private static int IniInt(SimHost h, string name, int fallback)
        {
            string? v = Params.Get(h, "SQL", name);
            return
                v != null
                && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                ? n
                : fallback;
        }

        private static int TextWidth(SimHost h) => Math.Max(1, Math.Min(255, IniInt(h, "TextColWidth", 255)));

        // [SQL]DefaultTimestampFormat 0..13: the .NET DateTime format
        private static readonly string[] DateFormats =
        {
            "t",
            "T",
            "d",
            "D",
            "g",
            "G",
            "f",
            "F",
            "m",
            "y",
            "r",
            "s",
            "u",
            "U",
        };

        private static string DateFormat(SimHost h) =>
            DateFormats[Math.Max(0, Math.Min(13, IniInt(h, "DefaultTimestampFormat", 11)))];

        /// <summary>A database value as the text Cicode gets: NULL is "", a byte array
        /// is "", a date and time takes the timestamp format, the text is cut to [SQL]TextColWidth.</summary>
        internal static int Text(object? v, out bool isNull, out string text, int width, string dateFormat)
        {
            text = "";
            isNull = true;
            if (v == null)
                return SqlErr.EndOfData;
            if (v is DBNull)
                return 0;
            isNull = false;
            int result = 0;
            string s;
            if (v is DateTime dt)
            {
                s = dt.ToString();
                if (!string.IsNullOrWhiteSpace(dateFormat))
                {
                    try
                    {
                        s = dt.ToString(dateFormat);
                    }
                    catch (FormatException)
                    {
                        result = SqlErr.WrongArgument;
                    }
                }
            }
            else if (
                v is string
                || v is bool
                || v is byte
                || v is char
                || v is DateTimeOffset
                || v is decimal
                || v is double
                || v is float
                || v is Guid
                || v is short
                || v is int
                || v is long
            )
                s = v.ToString() ?? "";
            else
                s = ""; // (a TimeSpan, an unsigned integer ...: the runtime's own chain tests
            // 'not null' before those types, so they read as "" there too)
            if (s.Length > width)
                s = s.Substring(0, width);
            text = s;
            return result;
        }

        /// <summary>A column's width for a format.</summary>
        internal static int Width(Type? type, int size, int textWidth)
        {
            if (type == null)
                return 1;
            if (type == typeof(string) || type == typeof(byte[]))
                return size >= 1 && size <= 2666 ? Math.Min(size, textWidth) : textWidth;
            if (type == typeof(bool))
                return 6;
            if (type == typeof(byte) || type == typeof(char))
                return 1;
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan))
                return 26;
            if (type == typeof(decimal) || type == typeof(Guid))
                return 39;
            if (type == typeof(double) || type == typeof(long) || type == typeof(ulong))
                return 22;
            if (type == typeof(float))
                return 13;
            if (type == typeof(short) || type == typeof(ushort))
                return 6;
            if (type == typeof(int) || type == typeof(uint))
                return 11;
            if (type == typeof(object))
                return 12;
            return 1;
        }

        private static readonly Regex FieldPattern = new Regex(
            "\\s*Field(?<colnum>\\d+)\\s*",
            RegexOptions.IgnoreCase
        );

        private static int FieldByPattern(string name)
        {
            var m = FieldPattern.Match(name);
            return m.Success && int.TryParse(m.Groups["colnum"].Value, out int n) ? n - 1 : -1;
        }

        // ------------------------------------------------------------------ connection strings
        /// <summary>The attributes of a connection string (';' separated, quotes kept together), as
        /// the runtime reads them.</summary>
        internal static List<KeyValuePair<string, string>> Attributes(string s)
        {
            var parts = new List<string>();
            var quotes = new Stack<char>();
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\'')
                {
                    if (quotes.Count > 0 && quotes.Peek() == c)
                        quotes.Pop();
                    else
                        quotes.Push(c);
                }
                else if (c == ';' && quotes.Count == 0)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start));
            var r = new List<KeyValuePair<string, string>>();
            foreach (string p in parts)
            {
                if (p.Trim().Length == 0)
                    continue;
                int eq = -1;
                for (int j = 0; j < p.Length; j++)
                {
                    if (p[j] != '=')
                        continue;
                    if (j + 1 < p.Length && p[j + 1] == '=')
                    {
                        j++;
                        continue;
                    }
                    eq = j;
                    break;
                }
                r.Add(
                    eq < 0
                        ? new KeyValuePair<string, string>(p.Trim(), "")
                        : new KeyValuePair<string, string>(p.Substring(0, eq).Trim(), p.Substring(eq + 1))
                );
            }
            return r;
        }

        private static string Attribute(List<KeyValuePair<string, string>> attrs, params string[] keys)
        {
            foreach (var kv in attrs)
            foreach (string k in keys)
                if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase))
                {
                    // one layer of quotes is syntax, not identity: Database="Hist" is Database=Hist
                    string v = kv.Value.Trim();
                    if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0])
                        v = v.Substring(1, v.Length - 2).Trim();
                    return v;
                }
            return "";
        }

        private static string Without(string s, params string[] keys)
        {
            var sb = new StringBuilder();
            foreach (var kv in Attributes(s))
            {
                bool drop = false;
                foreach (string k in keys)
                    if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase))
                        drop = true;
                if (drop)
                    continue;
                if (sb.Length > 0)
                    sb.Append(';');
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        /// <summary>What a connection string connects to, without its credentials.</summary>
        public static string Identity(string connectionString)
        {
            var a = Attributes(connectionString ?? "");
            var parts = new List<string>();
            void Add(string label, params string[] keys)
            {
                string v = Attribute(a, keys);
                if (v.Length > 0)
                    parts.Add(label + "=" + v);
            }
            Add("DSN", "DSN");
            Add("FileDSN", "FILEDSN");
            Add("Driver", "Driver");
            Add("Provider", "Provider");
            Add("Server", "Server", "Data Source", "Address", "Addr", "Network Address", "Host", "Hostname");
            Add("Database", "Database", "Initial Catalog", "DBQ", "DefaultDir");
            if (parts.Count > 0)
                return string.Join("; ", parts);
            return Without(
                    connectionString ?? "",
                    "UID",
                    "User ID",
                    "User",
                    "UserName",
                    "PWD",
                    "Password",
                    "SCADA Data Provider",
                    "SCADA Connection Timeout",
                    "SCADA Query Timeout"
                )
                .Trim();
        }

        private static readonly HashSet<string> ReadWords = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            "SELECT",
            "WITH",
            "SHOW",
            "DESCRIBE",
            "DESC",
            "EXPLAIN",
        };

        private static readonly HashSet<string> WriteWords = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            "INSERT",
            "UPDATE",
            "DELETE",
            "MERGE",
            "DROP",
            "ALTER",
            "CREATE",
            "TRUNCATE",
            "INTO",
            "EXEC",
            "EXECUTE",
            "CALL",
            "GRANT",
            "REVOKE",
            "REPLACE",
            "UPSERT",
            "RENAME",
            "BACKUP",
            "RESTORE",
            "SHUTDOWN",
            "KILL",
            "DBCC",
        };

        /// <summary>Can this statement change the database? Errs on the safe side.</summary>
        public static bool Changes(string statement)
        {
            string s = statement ?? "";
            var words = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\'' || c == '"' || c == '`')
                {
                    int j = i + 1;
                    while (j < s.Length && s[j] != c)
                        j++;
                    i = j + 1;
                }
                else if (c == '[')
                {
                    int j = s.IndexOf(']', i + 1);
                    i = j < 0 ? s.Length : j + 1;
                }
                else if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
                {
                    int j = s.IndexOf('\n', i);
                    i = j < 0 ? s.Length : j + 1;
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int j = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = j < 0 ? s.Length : j + 2;
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    int j = i;
                    while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_'))
                        j++;
                    words.Add(s.Substring(i, j - i));
                    i = j;
                }
                else if (c == ';')
                {
                    words.Add(";");
                    i++;
                }
                else
                    i++;
            }
            bool first = true;
            foreach (string w in words)
            {
                if (w == ";")
                {
                    first = true;
                    continue;
                }
                if (first)
                {
                    first = false;
                    if (!ReadWords.Contains(w))
                        return true;
                    continue;
                }
                if (
                    WriteWords.Contains(w) && !string.Equals(w, "REPLACE", StringComparison.OrdinalIgnoreCase)
                )
                    return true;
            }
            return false;
        }

        private static DbConnection? Provide(SimHost h, string provider, string connectionString)
        {
            if (h.SqlFactory != null)
                return h.SqlFactory(provider, connectionString);
#if NETFRAMEWORK
            switch (provider)
            {
                case "oledb":
                    return new System.Data.OleDb.OleDbConnection(connectionString);
                case "sqlclient":
                    return new System.Data.SqlClient.SqlConnection(connectionString);
                default:
                    return new System.Data.Odbc.OdbcConnection(connectionString);
            }
#else
            return null;
#endif
        }

        /// <summary>A new connection: the connection object, not opened. -1 and the
        /// data error when it cannot be made.</summary>
        private static SqlConn? Create(SimHost h, string connectionString, bool newStyle, out int error)
        {
            var w = h.SqlData;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                error = SqlErr.WrongArgument;
                SetError(h, null, -1, error, "Connection string is Empty or Null");
                return null;
            }
            int max = Math.Max(0, Math.Min(10000, IniInt(h, "MaxConnections", 10)));
            if (max > 0 && w.Count<SqlConn>() >= max)
            {
                error = SqlErr.TooManyConnections;
                SetError(h, null, -1, error, "Too many existing connections");
                return null;
            }
            var attrs = Attributes(connectionString);
            string provider = Attribute(attrs, "SCADA Data Provider").ToLowerInvariant();
            if (provider != "oledb" && provider != "sqlclient")
                provider = "odbc";
            string connTimeout = Attribute(attrs, "SCADA Connection Timeout").ToLowerInvariant();
            string queryTimeout = Attribute(attrs, "SCADA Query Timeout").ToLowerInvariant();
            string clean = Without(
                connectionString,
                "SCADA Data Provider",
                "SCADA Connection Timeout",
                "SCADA Query Timeout"
            );
            string timeoutKey = "";
            foreach (
                string k in new[] { "Connection Timeout", "Connect Timeout", "ConnectTimeout", "Timeout" }
            )
            foreach (var kv in attrs)
                if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase))
                    timeoutKey = k;
            DbConnection? db;
            try
            {
                db = Provide(h, provider, clean);
            }
            catch (Exception ex)
            {
                error = SqlErr.Software;
                SetError(h, null, -1, error, ex.Message);
                return null;
            }
            if (db == null)
            {
                error = SqlErr.Software;
                SetError(
                    h,
                    null,
                    -1,
                    error,
                    "This build of the simulator has no " + provider.ToUpperInvariant() + " provider"
                );
                return null;
            }
            int qt = Math.Max(-1, Math.Min(14400, IniInt(h, "QueryTimeout", -1)));
            var c = new SqlConn(db, queryTimeout, connTimeout, timeoutKey, qt)
            {
                ConnString = connectionString,
                Identity = Identity(connectionString),
                Provider = provider,
                NewStyle = newStyle,
            };
            if (w.Register(c) < 0)
            {
                error = SqlErr.Software;
                SetError(h, null, -1, error, "Connection registeration problem");
                try
                {
                    db.Dispose();
                }
                catch (Exception) { } // swallow-ok: a connection that was never opened
                return null;
            }
            error = 0;
            return c;
        }

        /// <summary>The statement a call runs: the given
        /// one, else the connection's (SQLSet / SQLAppend), else the query's with its parameters.</summary>
        private static int Statement(
            SimHost h,
            SqlConn c,
            long handle,
            string given,
            out string statement,
            out List<SqlParam>? pars
        )
        {
            statement = "";
            pars = null;
            if (!string.IsNullOrWhiteSpace(given))
            {
                statement = given;
                return 0;
            }
            if (handle == c.Handle)
            {
                // (no SQLSet text: the runtime has no default statement proxy then, error 2)
                if (string.IsNullOrWhiteSpace(c.DefaultStatement))
                    return SqlErr.WrongArgument;
                statement = c.DefaultStatement;
                return 0;
            }
            var q = h.SqlData.Get<SqlQuery>(handle);
            if (q == null || q.Conn != c.Handle)
                return SqlErr.WrongArgument;
            statement = q.Statement;
            if (q.Params.Count > 0)
            {
                pars = new List<SqlParam>();
                foreach (var p in q.Params)
                    pars.Add(
                        new SqlParam
                        {
                            Name = p.Name,
                            Value = p.Value,
                            Type = p.Type,
                        }
                    );
            }
            return 0;
        }

        /// <summary>The connection of a connection or query handle.</summary>
        private static SqlConn? ConnOf(SimHost h, long handle)
        {
            var c = h.SqlData.Get<SqlConn>(handle);
            if (c != null)
                return c;
            var q = h.SqlData.Get<SqlQuery>(handle);
            return q != null ? h.SqlData.Get<SqlConn>(q.Conn) : null;
        }

        /// <summary>What every finished operation does first: the
        /// connection's state, the emptied cache, the error to SQLErrMsg.</summary>
        private static int Done(SimHost h, SqlConn c, SqlOut o)
        {
            c.State = o.State;
            if (o.ClearCache)
                c.ClearCache();
            if (o.Error != 0)
                SetError(h, c, c.Handle, o.Error, o.Thrown ? o.Description : SqlErr.Describe(o.Error));
            return o.Error;
        }

        /// <summary>Finish an operation on the connection's own recordset.</summary>
        private static int DoneRecordset(SimHost h, SqlConn c, SqlOut o)
        {
            c.State = o.State;
            if (o.ClearCache)
                c.ClearCache();
            if (o.Error != 0)
                SetError(
                    h,
                    null,
                    o.Thrown ? c.Handle : -1,
                    o.Error,
                    o.Thrown ? o.Description : SqlErr.Describe(o.Error)
                );
            return o.Error;
        }

        private static Pending Wait(Task<SqlOut> task, Func<SqlOut, Value> result) =>
            new Pending { Poll = () => task.IsCompleted, Result = () => result(Out(task)) };

        private static SqlOut Out(Task<SqlOut> task)
        {
            if (task.Status == TaskStatus.RanToCompletion)
                return task.Result;
            return new SqlOut
            {
                Error = SqlErr.Software,
                Thrown = true,
                Description = task.Exception?.GetBaseException().Message ?? "The operation did not finish",
            };
        }

        private static Value Go(CicodeTask t, string fn, Pending p) =>
            p.Poll == null ? p.Result() : OutsideAsk.Await(t, fn, p);

        /// <summary>Run <paramref name="then"/> when the database may be connected to; ask first
        /// in mode Ask (a database not on the whitelist), refuse with a database error.</summary>
        private static Value WhenConnectAllowed(
            SimHost h,
            CicodeTask t,
            string fn,
            string identity,
            Func<bool, Pending> then
        )
        {
            OutsideAsk.PumpAnswers(h);
            bool? d = h.Outside.SqlConnectDecision(identity);
            if (d.HasValue)
                return Go(t, fn, then(d.Value));
            return OutsideAsk.Then(h, t, "sql", "connect", identity, fn, then);
        }

        private static Value WhenStatementAllowed(
            SimHost h,
            CicodeTask t,
            string fn,
            SqlConn c,
            string statement,
            Func<bool, Pending> then
        )
        {
            if (!Changes(statement))
                return Go(t, fn, then(true));
            OutsideAsk.PumpAnswers(h);
            bool? d = h.Outside.SqlWriteDecision(c.Identity);
            if (d.HasValue)
                return Go(t, fn, then(d.Value));
            return OutsideAsk.Then(h, t, "sql", "write", c.Identity, statement, then);
        }

        private static void Denied(SimHost h, CicodeTask t, SqlObject? o, int handle, string what)
        {
            SetError(h, o, handle, SqlErr.Database, what);
            h.Note(t, what);
        }

        // ------------------------------------------------------------------ connections
        [Builtin(
            "SQLConnect",
            Doc = "1192464",
            Blocking = true,
            Errors = "307 the database cannot be reached, or the connection was not allowed (raised); 274 an empty connection string (raised); 271 [SQL]MaxConnections connections exist (raised); 282 foreground code (raised)",
            Effects = "connects to the database (options sql 'ask' / 'on')",
            Notes = "A deliberate deviation: ctsim/options sql. 'off' (the default): no database is reached, every connection fails with 307 after one turn and the call counts as not simulated. 'ask': the viewer is asked before a database that is not on the whitelist is connected to (denied, or no viewer shows dialogs: 307). 'on': connects. ODBC unless the string's 'SCADA Data Provider' attribute says OLEDB or SQLClient; 'SCADA Connection Timeout' and 'SCADA Query Timeout' as in the runtime. SQL errors raise without the halt flag."
        )]
        private static Value SQLConnect(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLConnect", a, Outside.SQLConnectOff(h, t, a));
            if (Foreground(t))
                return W.I(-1);
            string cs = A.S(a.Get(0));
            string id = Identity(cs);
            Trace(h, t, null, 0, "SQLConnect(" + id + ")");
            return WhenConnectAllowed(h, t, "SQLConnect", id, allowed =>
            {
                if (!allowed)
                {
                    Denied(h, t, null, -1, "SQLConnect: the simulator was not allowed to connect to " + id);
                    return Pending.Now(() =>
                    {
                        Raise(t, SqlErr.Database, false);
                        return W.I(-1);
                    });
                }
                var c = Create(h, cs, false, out int e);
                if (c == null)
                    return Pending.Now(() =>
                    {
                        Raise(t, e, false);
                        return W.I(-1);
                    });
                return Wait(c.Open(), o =>
                {
                    int err = Done(h, c, o);
                    // (SQL turned off while the connection opened: it is closed already, no handle)
                    if (err == 0 && c.Disposed)
                        err = SqlErr.Cancelled;
                    if (err != 0)
                    {
                        h.SqlData.RemoveConnection(c);
                        c.Shutdown();
                        h.Note(
                            t,
                            "SQLConnect: " + id + " cannot be connected to: " + h.SqlData.Last.Description
                        );
                        Raise(t, err, false);
                        return W.I(-1);
                    }
                    c.LegacySelect = "";
                    h.Note(t, "SQLConnect: connected to " + id);
                    return W.I(c.Handle);
                });
            });
        }

        [Builtin(
            "SQLCreate",
            Doc = "1192465",
            Errors = "274 an empty connection string (raised); 271 [SQL]MaxConnections connections exist (raised); 256 the connection object cannot be made (raised)",
            Notes = "The connection object, not connected (SQLOpen connects). Options sql 'off' (the default): no object, 307 raised, -1, counted as not simulated."
        )]
        private static Value SQLCreate(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLCreate", a, Outside.SQLCreateOff(h, t, a));
            var c = Create(h, A.S(a.Get(0)), true, out int e);
            if (c == null)
            {
                Raise(t, e, false);
                return W.I(-1);
            }
            Trace(h, t, c, 0, "SQLCreate(" + c.Identity + ") = " + c.Handle);
            return W.I(c.Handle);
        }

        [Builtin(
            "SQLOpen",
            Doc = "1192481",
            Blocking = true,
            Errors = "274 no such connection (raised, returned); 307 the database cannot be reached, or the connection was not allowed (raised, returned); 282 foreground code (raised)",
            Effects = "connects to the database (options sql 'ask' / 'on')",
            Notes = "Options sql 'ask' asks before a database that is not on the whitelist is connected to. 'off': 269, counted as not simulated."
        )]
        private static Value SQLOpen(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLOpen", a, Outside.SqlStatus(h, t, a));
            if (Foreground(t))
                return W.I(0);
            long hd = A.I(a.Get(0));
            var c = h.SqlData.Get<SqlConn>(hd);
            if (c == null)
            {
                NoHandle(h, t, hd, false);
                return W.I(Codes.InvalidArg);
            }
            Trace(h, t, c, 0, "SQLOpen(" + hd + ")");
            return WhenConnectAllowed(h, t, "SQLOpen", c.Identity, allowed =>
            {
                if (!allowed)
                {
                    Denied(
                        h,
                        t,
                        c,
                        c.Handle,
                        "SQLOpen: the simulator was not allowed to connect to " + c.Identity
                    );
                    return Pending.Now(() => W.I(Raise(t, SqlErr.Database, false)));
                }
                return Wait(c.Open(), o =>
                {
                    int err = Done(h, c, o);
                    if (err == 0 && c.Disposed)
                        err = SqlErr.Cancelled;
                    if (err == 0)
                    {
                        c.LegacySelect = "";
                        h.Note(t, "SQLOpen: connected to " + c.Identity);
                    }
                    else
                        h.Note(
                            t,
                            "SQLOpen: " + c.Identity + " cannot be connected to: " + c.Last.Description
                        );
                    return W.I(Raise(t, err, false));
                });
            });
        }

        private static Value CloseLike(SimHost h, CicodeTask t, ArgList a, string fn, bool dispose)
        {
            if (Off(h))
                return Stub(h, t, fn, a, Outside.SqlStatus(h, t, a));
            if (Foreground(t))
                return W.I(0);
            long hd = A.I(a.Get(0));
            var c = h.SqlData.Get<SqlConn>(hd);
            if (c == null)
            {
                NoHandle(h, t, hd, false);
                return W.I(Codes.InvalidArg);
            }
            Trace(h, t, c, 0, fn + "(" + hd + ")");
            if (dispose)
                h.SqlData.RemoveConnection(c);
            return Go(t, fn, Wait(dispose ? c.CloseAndDispose() : c.Close(), o =>
                {
                    int err = Done(h, c, o);
                    if (err == 0)
                    {
                        c.LegacySelect = "";
                        // the two styles do not mix: SQLConnect / SQLDisconnect, SQLCreate / SQLOpen / SQLClose / SQLDispose
                        // (the call's result only: SQLErrMsg does not get it)
                        if (dispose == c.NewStyle)
                            err = SqlErr.MixedStyles;
                    }
                    return W.I(Raise(t, err, false));
                }));
        }

        [Builtin(
            "SQLDisconnect",
            Doc = "1192466",
            Blocking = true,
            Errors = "274 no such connection (raised, returned); 307 the close failed (raised, returned); 457 a connection of SQLCreate (raised, returned; it is closed and gone all the same); 282 foreground code (raised)",
            Effects = "closes the connection (a pending transaction is rolled back); the connection and its queries are gone",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLDisconnect(SimHost h, CicodeTask t, ArgList a) =>
            CloseLike(h, t, a, "SQLDisconnect", true);

        [Builtin(
            "SQLClose",
            Doc = "1192462",
            Blocking = true,
            Errors = "274 no such connection (raised, returned); 307 the close failed (raised, returned); 457 a connection of SQLConnect (raised, returned; it is closed all the same); 282 foreground code (raised)",
            Effects = "closes the connection (a pending transaction is rolled back); the connection object stays",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLClose(SimHost h, CicodeTask t, ArgList a) =>
            CloseLike(h, t, a, "SQLClose", false);

        [Builtin(
            "SQLDispose",
            Doc = "1192467",
            Errors = "274 no such connection (raised, returned); 278 the connection is still connected (raised, returned)",
            Effects = "the connection object and its queries are gone",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLDispose(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLDispose", a, Outside.SqlStatus(h, t, a));
            long hd = A.I(a.Get(0));
            var c = h.SqlData.Get<SqlConn>(hd);
            if (c == null)
            {
                SetError(h, null, (int)hd, SqlErr.NoConnection, "The connection proxy doesn't exist");
                return W.I(Raise(t, SqlErr.NoConnection, false));
            }
            if (c.Connected)
            {
                SetError(h, null, c.Handle, SqlErr.StillActive, "This connection is still connected");
                return W.I(Raise(t, SqlErr.StillActive, false));
            }
            Trace(h, t, c, 0, "SQLDispose(" + hd + ")");
            h.SqlData.RemoveConnection(c);
            c.Shutdown();
            return W.I(0);
        }

        // ------------------------------------------------------------------ statements
        [Builtin(
            "SQLExec",
            Doc = "1192470",
            Blocking = true,
            Errors = "274 no such connection or query, or no statement (raised, returned); 307 the database refuses the statement, or it was not allowed (raised, returned); 299 cancelled (raised, returned); 282 foreground code (raised)",
            Effects = "runs the statement on the database (options sql 'ask' / 'on'); the connection's recordset is the result, before its first row",
            Notes = "An empty statement runs the one of SQLSet / SQLAppend (a connection handle) or the query's with its parameters (a query handle). Options sql 'ask' asks before a statement that can change the database (anything but a plain SELECT) unless the database is on the whitelist or was allowed for the session; denied: 307. 'off': 269, counted as not simulated."
        )]
        private static Value SQLExec(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLExec", a, Outside.SqlStatus(h, t, a));
            if (Foreground(t))
                return W.I(0);
            long hd = A.I(a.Get(0));
            var c = ConnOf(h, hd);
            if (c == null)
            {
                NoHandle(h, t, hd, false);
                return W.I(Codes.InvalidArg);
            }
            int e = Statement(h, c, hd, A.S(a.Get(1)), out string stmt, out var pars);
            if (e != 0)
                return W.I(Raise(t, e, false));
            Trace(h, t, c, 0, "SQLExec(" + hd + ", " + stmt + ")");
            return WhenStatementAllowed(h, t, "SQLExec", c, stmt, allowed =>
            {
                if (!allowed)
                {
                    Denied(
                        h,
                        t,
                        c,
                        c.Handle,
                        "SQLExec: the simulator was not allowed to change " + c.Identity
                    );
                    return Pending.Now(() => W.I(Raise(t, SqlErr.Database, false)));
                }
                return Wait(c.Exec(stmt, pars), o =>
                {
                    int err = Done(h, c, o);
                    Merge(c, o.Batch);
                    if (o.RowCountSet)
                        c.Affected = o.RowCount;
                    if (err == 0)
                        c.LegacySelect = stmt;
                    return W.I(Raise(t, err, false));
                });
            });
        }

        private static void Merge(SqlConn c, SqlBatch? b)
        {
            if (b == null)
                return;
            if (b.Header)
            {
                c.ColNames = b.Names;
                c.ColTypes = b.Types;
                c.ColSizes = b.Sizes;
            }
            c.Rows.AddRange(b.Rows);
        }

        [Builtin(
            "SQLGetRecordset",
            Doc = "1192473",
            Blocking = true,
            Errors = "274 no such connection or query, or no statement (raised); 271 [SQL]MaxRecordsets recordsets exist (raised); 307 the database refuses the statement, or it was not allowed (raised); 299 cancelled (raised); 282 foreground code (raised)",
            Effects = "runs the statement on the database (options sql 'ask' / 'on'); a new recordset holds the whole result",
            Notes = "-1 without an error when the result has no columns or no rows. Options sql 'ask' asks before a statement that can change the database. 'off': -1 and 269, counted as not simulated."
        )]
        private static Value SQLGetRecordset(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLGetRecordset", a, Outside.SqlHandle(h, t, a));
            if (Foreground(t))
                return W.I(-1);
            long hd = A.I(a.Get(0));
            var c = ConnOf(h, hd);
            if (c == null)
            {
                NoHandle(h, t, hd, false);
                return W.I(-1);
            }
            int e = Statement(h, c, hd, A.S(a.Get(1)), out string stmt, out var pars);
            if (e != 0)
            {
                Raise(t, e, false);
                return W.I(-1);
            }
            Trace(h, t, c, 0, "SQLGetRecordset(" + hd + ", " + stmt + ")");
            return WhenStatementAllowed(h, t, "SQLGetRecordset", c, stmt, allowed =>
            {
                if (!allowed)
                {
                    Denied(
                        h,
                        t,
                        c,
                        c.Handle,
                        "SQLGetRecordset: the simulator was not allowed to change " + c.Identity
                    );
                    return Pending.Now(() =>
                    {
                        Raise(t, SqlErr.Database, false);
                        return W.I(-1);
                    });
                }
                int max = Math.Max(0, Math.Min(10000, IniInt(h, "MaxRecordsets", 30)));
                if (max > 0 && h.SqlData.Count<SqlRecordset>() >= max)
                {
                    SetError(h, null, -1, SqlErr.TooManyRecordsets, "Too many existing recordsets");
                    return Pending.Now(() =>
                    {
                        Raise(t, SqlErr.TooManyRecordsets, false);
                        return W.I(-1);
                    });
                }
                var rs = new SqlRecordset
                {
                    Conn = c.Handle,
                    Statement = stmt,
                    ConnString = c.ConnString,
                };
                if (h.SqlData.Register(rs) < 0)
                {
                    SetError(h, null, -1, SqlErr.Software, "Recordset registeration problem");
                    return Pending.Now(() =>
                    {
                        Raise(t, SqlErr.Software, false);
                        return W.I(-1);
                    });
                }
                return Wait(c.GetRecordset(stmt, pars), o =>
                {
                    int err = Done(h, c, o);
                    if (o.RowCountSet)
                        c.Affected = o.RowCount;
                    if (err != 0 || o.Table == null || o.Table.Columns.Count <= 0 || o.Table.Rows.Count <= 0)
                    {
                        h.SqlData.Objects.Remove(rs.Handle);
                        Raise(t, err, false);
                        return W.I(-1);
                    }
                    rs.Table = o.Table;
                    return W.I(rs.Handle);
                });
            });
        }

        [Builtin(
            "SQLCall",
            Doc = "1192460",
            Blocking = true,
            Errors = "274 no such connection or query, or no statement (raised); 307 the database refuses the statement, or it was not allowed (raised); 299 cancelled (raised); 282 foreground code (raised)",
            Effects = "runs the statement on the database (options sql 'ask' / 'on')",
            Notes = "The number of rows the statement changed, -1 on an error. Options sql 'ask' asks before a statement that can change the database. 'off': -1 and 269, counted as not simulated."
        )]
        private static Value SQLCall(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLCall", a, Outside.SqlHandle(h, t, a));
            if (Foreground(t))
                return W.I(-1);
            long hd = A.I(a.Get(0));
            var c = ConnOf(h, hd);
            if (c == null)
            {
                NoHandle(h, t, hd, false);
                return W.I(-1);
            }
            int e = Statement(h, c, hd, A.S(a.Get(1)), out string stmt, out var pars);
            if (e != 0)
            {
                Raise(t, e, false);
                return W.I(-1);
            }
            Trace(h, t, c, 0, "SQLCall(" + hd + ", " + stmt + ")");
            return WhenStatementAllowed(h, t, "SQLCall", c, stmt, allowed =>
            {
                if (!allowed)
                {
                    Denied(
                        h,
                        t,
                        c,
                        c.Handle,
                        "SQLCall: the simulator was not allowed to change " + c.Identity
                    );
                    return Pending.Now(() =>
                    {
                        Raise(t, SqlErr.Database, false);
                        return W.I(-1);
                    });
                }
                return Wait(c.NonQuery(stmt, pars), o =>
                {
                    int err = Done(h, c, o);
                    if (o.RowCountSet)
                        c.Affected = o.RowCount;
                    if (err != 0)
                    {
                        Raise(t, err, false);
                        return W.I(-1);
                    }
                    return W.I(o.RowCount);
                });
            });
        }

        [Builtin(
            "SQLGetScalar",
            Doc = "1192474",
            Blocking = true,
            Errors = "274 no such connection or query, or no statement (raised); 307 the database refuses the statement, or it was not allowed (raised); 294 the statement gives no value (raised); 299 cancelled (raised); 282 foreground code (raised)",
            Effects = "runs the statement on the database (options sql 'ask' / 'on'); writes 1 into the third argument when the value is NULL or the call failed, else 0",
            Notes = "The first column of the first row as text. Options sql 'ask' asks before a statement that can change the database. 'off': \"\" and 269, counted as not simulated."
        )]
        private static Value SQLGetScalar(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLGetScalar", a, Outside.SqlText(h, t, a));
            Value isNull = a.Count > 2 ? a.Get(2) : Value.Nothing;
            if (Foreground(t))
            {
                W.Out(t, isNull, 1);
                return W.Empty;
            }
            long hd = A.I(a.Get(0));
            var c = ConnOf(h, hd);
            if (c == null)
            {
                W.Out(t, isNull, 1);
                NoHandle(h, t, hd, false);
                return W.Empty;
            }
            int e = Statement(h, c, hd, A.S(a.Get(1)), out string stmt, out var pars);
            if (e != 0)
            {
                W.Out(t, isNull, 1);
                Raise(t, e, false);
                return W.Empty;
            }
            Trace(h, t, c, 0, "SQLGetScalar(" + hd + ", " + stmt + ")");
            int width = TextWidth(h);
            string fmt = DateFormat(h);
            return WhenStatementAllowed(h, t, "SQLGetScalar", c, stmt, allowed =>
            {
                if (!allowed)
                {
                    Denied(
                        h,
                        t,
                        c,
                        c.Handle,
                        "SQLGetScalar: the simulator was not allowed to change " + c.Identity
                    );
                    return Pending.Now(() =>
                    {
                        W.Out(t, isNull, 1);
                        Raise(t, SqlErr.Database, false);
                        return W.Empty;
                    });
                }
                return Wait(c.Scalar(stmt, pars, width, fmt), o =>
                {
                    int err = Done(h, c, o);
                    c.Affected = 0;
                    W.Out(t, isNull, err != 0 || o.IsNull ? 1 : 0);
                    Raise(t, err, false);
                    return W.S(A.Cap(o.Text));
                });
            });
        }

        private static Value SetOrAppend(SimHost h, CicodeTask t, ArgList a, bool append)
        {
            if (Off(h))
                return Stub(h, t, append ? "SQLAppend" : "SQLSet", a, Outside.SqlStatus(h, t, a));
            long hd = A.I(a.Get(0));
            var c = ConnOf(h, hd);
            if (c == null)
            {
                NoHandle(h, t, hd, true);
                return W.I(Codes.InvalidArg);
            }
            string text = A.S(a.Get(1));
            if (hd == c.Handle)
                c.DefaultStatement = append ? c.DefaultStatement + text : text;
            else
            {
                var q = h.SqlData.Get<SqlQuery>(hd)!;
                q.Statement = append ? q.Statement + text : text;
            }
            return W.I(0);
        }

        [Builtin(
            "SQLSet",
            Doc = "1192491",
            Errors = "274 no such connection or query (raised quietly, returned)",
            Notes = "The statement of the connection (run by SQLExec and the like with an empty statement) or of a query. Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLSet(SimHost h, CicodeTask t, ArgList a) => SetOrAppend(h, t, a, false);

        [Builtin(
            "SQLAppend",
            Doc = "1192458",
            Errors = "274 no such connection or query (raised quietly, returned)",
            Notes = "Adds text to the statement of SQLSet. Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLAppend(SimHost h, CicodeTask t, ArgList a) => SetOrAppend(h, t, a, true);

        [Builtin(
            "SQLQueryCreate",
            Doc = "1192487",
            Errors = "274 no such connection (raised quietly)",
            Notes = "A query of the connection: its own statement (SQLSet / SQLAppend) and parameters (SQLParamsSetAs...). Options sql 'off': -1 and 269, counted as not simulated."
        )]
        private static Value SQLQueryCreate(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLQueryCreate", a, Outside.SqlHandle(h, t, a));
            long hd = A.I(a.Get(0));
            var c = h.SqlData.Get<SqlConn>(hd);
            if (c == null)
            {
                NoHandle(h, t, hd, true);
                return W.I(-1);
            }
            var q = new SqlQuery { Conn = c.Handle };
            if (h.SqlData.Register(q) < 0)
            {
                SetError(h, c, c.Handle, SqlErr.Software, "Statement registeration problem");
                Raise(t, SqlErr.Software, true);
                return W.I(-1);
            }
            return W.I(q.Handle);
        }

        [Builtin(
            "SQLQueryDispose",
            Doc = "1192488",
            Errors = "274 no such query (raised quietly, returned)",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLQueryDispose(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLQueryDispose", a, Outside.SqlStatus(h, t, a));
            long hd = A.I(a.Get(0));
            var q = h.SqlData.Get<SqlQuery>(hd);
            if (q == null)
            {
                SetError(h, null, (int)Math.Max(-1, Math.Min(hd, 32767)), SqlErr.WrongArgument, "No Manager");
                return W.I(Raise(t, SqlErr.WrongArgument, true));
            }
            h.SqlData.Objects.Remove(q.Handle);
            return W.I(0);
        }

        private static Value ParamSet(SimHost h, CicodeTask t, ArgList a, string fn, Func<SqlParam> make)
        {
            if (Off(h))
                return Stub(h, t, fn, a, Outside.SqlStatus(h, t, a));
            if (Foreground(t))
                return W.I(0);
            long hd = A.I(a.Get(0));
            var c = h.SqlData.Get<SqlConn>(hd);
            var q = c == null ? h.SqlData.Get<SqlQuery>(hd) : null;
            if (c == null && q == null)
            {
                NoHandle(h, t, hd, true);
                return W.I(Codes.InvalidArg);
            }
            var p = make();
            Trace(
                h,
                t,
                (SqlObject?)c ?? q,
                0,
                fn
                    + "("
                    + hd
                    + ", "
                    + p.Name
                    + ", "
                    + Convert.ToString(p.Value, CultureInfo.InvariantCulture)
                    + ")"
            );
            if (q != null)
            {
                q.Params.Add(p);
                return W.I(0);
            }
            return Go(t, fn, Wait(c!.AddParam(p), o => W.I(Raise(t, Done(h, c, o), true))));
        }

        [Builtin(
            "SQLParamsSetAsInt",
            Doc = "1192483",
            Blocking = true,
            Errors = "274 no such connection or query (raised quietly, returned); 282 foreground code (raised)",
            Notes = "Adds an integer parameter to the connection's or the query's statement. Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLParamsSetAsInt(SimHost h, CicodeTask t, ArgList a) =>
            ParamSet(h, t, a, "SQLParamsSetAsInt", () =>
                new SqlParam
                {
                    Name = A.S(a.Get(1)),
                    Value = (int)A.I(a.Get(2)),
                    Type = 5,
                }
            );

        [Builtin(
            "SQLParamsSetAsReal",
            Doc = "1192484",
            Blocking = true,
            Errors = "274 no such connection or query (raised quietly, returned); 282 foreground code (raised)",
            Notes = "Adds a real parameter. Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLParamsSetAsReal(SimHost h, CicodeTask t, ArgList a) =>
            ParamSet(h, t, a, "SQLParamsSetAsReal", () =>
                new SqlParam
                {
                    Name = A.S(a.Get(1)),
                    Value = A.R(a.Get(2)),
                    Type = 6,
                }
            );

        [Builtin(
            "SQLParamsSetAsString",
            Doc = "1192485",
            Blocking = true,
            Errors = "274 no such connection or query (raised quietly, returned); 282 foreground code (raised)",
            Notes = "Adds a string parameter; nStrType 0 the provider's default, 1 NVarChar, 2 NChar, 3 VarChar, 4 Char. Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLParamsSetAsString(SimHost h, CicodeTask t, ArgList a) =>
            ParamSet(h, t, a, "SQLParamsSetAsString", () =>
                new SqlParam
                {
                    Name = A.S(a.Get(1)),
                    Value = A.S(a.Get(2)),
                    Type = a.Count > 3 ? (int)A.Short(A.I(a.Get(3))) : 0,
                }
            );

        [Builtin(
            "SQLParamsClearAll",
            Doc = "1192482",
            Blocking = true,
            Errors = "274 no such connection or query (raised quietly, returned); 282 foreground code (raised)",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLParamsClearAll(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLParamsClearAll", a, Outside.SqlStatus(h, t, a));
            if (Foreground(t))
                return W.I(0);
            long hd = A.I(a.Get(0));
            var c = h.SqlData.Get<SqlConn>(hd);
            var q = c == null ? h.SqlData.Get<SqlQuery>(hd) : null;
            if (c == null && q == null)
            {
                NoHandle(h, t, hd, true);
                return W.I(Codes.InvalidArg);
            }
            if (q != null)
            {
                q.Params.Clear();
                return W.I(0);
            }
            return Go(
                t,
                "SQLParamsClearAll",
                Wait(c!.ClearParams(), o => W.I(Raise(t, Done(h, c, o), true)))
            );
        }

        // ------------------------------------------------------------------ transactions
        private static Value Tran(
            SimHost h,
            CicodeTask t,
            ArgList a,
            string fn,
            Func<SqlConn, Task<SqlOut>> op
        )
        {
            if (Off(h))
                return Stub(h, t, fn, a, Outside.SqlStatus(h, t, a));
            if (Foreground(t))
                return W.I(0);
            long hd = A.I(a.Get(0));
            var c = h.SqlData.Get<SqlConn>(hd);
            if (c == null)
            {
                NoHandle(h, t, hd, true);
                return W.I(Codes.InvalidArg);
            }
            Trace(h, t, c, 0, fn + "(" + hd + ")");
            return Go(t, fn, Wait(op(c), o =>
                {
                    int err = Done(h, c, o);
                    if (err == 0)
                        c.LegacySelect = "";
                    return W.I(Raise(t, err, true));
                }));
        }

        [Builtin(
            "SQLBeginTran",
            Doc = "1192459",
            Blocking = true,
            Errors = "274 no such connection (raised quietly, returned); 307 a transaction is pending, or the database refuses (raised quietly, returned); 282 foreground code (raised)",
            Effects = "starts a transaction on the database",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLBeginTran(SimHost h, CicodeTask t, ArgList a) =>
            Tran(h, t, a, "SQLBeginTran", c => c.BeginTran());

        [Builtin(
            "SQLCommit",
            Doc = "1192463",
            Blocking = true,
            Errors = "274 no such connection (raised quietly, returned); 307 no transaction, or the database refuses (raised quietly, returned); 282 foreground code (raised)",
            Effects = "commits the transaction on the database",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLCommit(SimHost h, CicodeTask t, ArgList a) =>
            Tran(h, t, a, "SQLCommit", c => c.EndTran(true));

        [Builtin(
            "SQLRollBack",
            Doc = "1192489",
            Blocking = true,
            Errors = "274 no such connection (raised quietly, returned); 307 no transaction, or the database refuses (raised quietly, returned); 282 foreground code (raised)",
            Effects = "rolls the transaction back on the database",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLRollBack(SimHost h, CicodeTask t, ArgList a) =>
            Tran(h, t, a, "SQLRollBack", c => c.EndTran(false));

        [Builtin(
            "SQLCancel",
            Doc = "1192461",
            Blocking = true,
            Errors = "274 no such connection (raised quietly, returned); 282 foreground code (raised)",
            Effects = "cancels the connection's running statement; the calls waiting behind it end with 299",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLCancel(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLCancel", a, Outside.SqlStatus(h, t, a));
            if (Foreground(t))
                return W.I(0);
            long hd = A.I(a.Get(0));
            var c = h.SqlData.Get<SqlConn>(hd);
            if (c == null)
            {
                NoHandle(h, t, hd, true);
                return W.I(Codes.InvalidArg);
            }
            Trace(h, t, c, 0, "SQLCancel(" + hd + ")");
            c.Cancel();
            c.LegacySelect = "";
            return W.I(0);
        }

        // ------------------------------------------------------------------ recordsets
        [Builtin(
            "SQLNext",
            Doc = "1192477",
            Blocking = true,
            Errors = "274 no such connection or recordset (raised quietly, returned); 294 no more rows (raised quietly, returned); 307 the database fails (raised quietly, returned); 282 foreground code (raised)",
            Effects = "moves to the next row (a connection's recordset reads three rows at a time from the database)",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLNext(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLNext", a, Outside.SqlStatus(h, t, a));
            if (Foreground(t))
                return W.I(0);
            long hd = A.I(a.Get(0));
            var c = h.SqlData.Get<SqlConn>(hd);
            if (c != null)
            {
                long next = Math.Max(0, c.Cursor + 1);
                if (next < c.Rows.Count)
                {
                    c.Cursor = next;
                    return W.I(0);
                }
                return Go(t, "SQLNext", Wait(c.Next(), o =>
                    {
                        int err = DoneRecordset(h, c, o);
                        if (err != SqlErr.EndOfData && !o.Thrown)
                        {
                            // the rows read must have the header's columns. A
                            // result without rows reads nothing, no header either: the runtime records
                            // data error 18 then (and SQLNext still answers 'no more rows'). A read the
                            // provider failed keeps the provider's message (nothing to merge)
                            var b = o.Batch;
                            bool same = b != null && b.Names.Count == c.ColNames.Count;
                            for (int i = 0; same && i < b!.Names.Count; i++)
                                same = b.Names[i] == c.ColNames[i];
                            if (!same)
                                SetError(
                                    h,
                                    null,
                                    c.Handle,
                                    SqlErr.Inconsistent,
                                    "Merging problem for connected data"
                                );
                            Merge(c, b);
                        }
                        long n = Math.Max(0, c.Cursor + 1);
                        if (n < c.Rows.Count)
                            c.Cursor = n;
                        else
                        {
                            c.Cursor = c.Rows.Count;
                            if (err == 0)
                                err = SqlErr.EndOfData;
                        }
                        return W.I(Raise(t, err, true));
                    }));
            }
            var rs = h.SqlData.Get<SqlRecordset>(hd);
            if (rs == null)
            {
                NoHandle(h, t, hd, true);
                return W.I(Codes.InvalidArg);
            }
            int rows = rs.Table?.Rows.Count ?? 0;
            rs.Cursor = Math.Max(0, rs.Cursor + 1);
            if (rs.Cursor >= rows)
            {
                rs.Cursor = rows;
                return W.I(Raise(t, SqlErr.EndOfData, true));
            }
            return W.I(0);
        }

        [Builtin(
            "SQLPrev",
            Doc = "1192486",
            Errors = "274 no such connection or recordset (raised quietly, returned); 294 before the first row (raised quietly, returned); 307 a connection's own recordset only goes forward (raised quietly, returned)",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLPrev(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLPrev", a, Outside.SqlStatus(h, t, a));
            long hd = A.I(a.Get(0));
            var c = h.SqlData.Get<SqlConn>(hd);
            if (c != null)
            {
                SetError(
                    h,
                    null,
                    c.Handle,
                    SqlErr.Database,
                    "SQLPrev is not supported on connected recordsets"
                );
                return W.I(Raise(t, SqlErr.Database, true));
            }
            var rs = h.SqlData.Get<SqlRecordset>(hd);
            if (rs == null)
            {
                NoHandle(h, t, hd, true);
                return W.I(Codes.InvalidArg);
            }
            int rows = rs.Table?.Rows.Count ?? 0;
            rs.Cursor--;
            if (rs.Cursor >= rows)
                rs.Cursor = rows - 1;
            if (rs.Cursor < 0)
            {
                rs.Cursor = -1;
                return W.I(Raise(t, SqlErr.EndOfData, true));
            }
            return W.I(0);
        }

        [Builtin(
            "SQLEnd",
            Doc = "1192468",
            Blocking = true,
            Errors = "274 no such connection or recordset (raised quietly, returned); 282 foreground code (raised)",
            Effects = "ends the connection's recordset (its reader is closed), or frees a recordset of SQLGetRecordset",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLEnd(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLEnd", a, Outside.SqlStatus(h, t, a));
            if (Foreground(t))
                return W.I(0);
            long hd = A.I(a.Get(0));
            var c = h.SqlData.Get<SqlConn>(hd);
            if (c != null)
            {
                Trace(h, t, c, 0, "SQLEnd(" + hd + ")");
                return Go(t, "SQLEnd", Wait(c.End(), o =>
                    {
                        int err = DoneRecordset(h, c, o);
                        c.ClearCache();
                        return W.I(Raise(t, err, true));
                    }));
            }
            var rs = h.SqlData.Get<SqlRecordset>(hd);
            if (rs == null)
            {
                NoHandle(h, t, hd, true);
                return W.I(Codes.InvalidArg);
            }
            h.SqlData.Objects.Remove(rs.Handle);
            rs.Table?.Dispose();
            rs.Table = null;
            return W.I(0);
        }

        /// <summary>The value of a field of the current (or, in a recordset of SQLGetRecordset,
        /// a given) row: the data error and the value. <paramref name="known"/> false: no
        /// connection or recordset has the handle.</summary>
        private static int Field(
            SimHost h,
            long handle,
            string field,
            long row,
            bool byPattern,
            out object? value,
            out bool known
        )
        {
            value = null;
            known = true;
            var c = h.SqlData.Get<SqlConn>(handle);
            if (c != null)
            {
                if (row >= 0)
                    return SqlErr.WrongArgument;
                if (c.Cursor < 0 || c.Cursor >= c.Rows.Count)
                    return SqlErr.EndOfData;
                var cells = c.Rows[(int)c.Cursor];
                int ix = -1;
                for (int i = 0; i < c.ColNames.Count && ix < 0; i++)
                    if (string.Compare(field, c.ColNames[i], StringComparison.CurrentCultureIgnoreCase) == 0)
                        ix = i;
                if (ix < 0 || ix >= cells.Length)
                {
                    ix = byPattern ? FieldByPattern(field) : -1;
                    if (ix < 0 || ix >= cells.Length)
                        return SqlErr.FieldNotFound;
                }
                value = cells[ix] ?? DBNull.Value;
                return 0;
            }
            var rs = h.SqlData.Get<SqlRecordset>(handle);
            if (rs == null)
            {
                known = false;
                return SqlErr.WrongArgument;
            }
            if (rs.Table == null)
                return SqlErr.Software;
            long r = row < 0 ? rs.Cursor : row;
            if (r < 0 || r >= rs.Table.Rows.Count)
                return SqlErr.EndOfData;
            int col = rs.Table.Columns.IndexOf(field);
            var items = rs.Table.Rows[(int)r].ItemArray;
            if (col < 0 || col >= items.Length)
            {
                col = byPattern ? FieldByPattern(field) : -1;
                if (col < 0 || col >= items.Length)
                    return SqlErr.FieldNotFound;
            }
            value = items[col] ?? DBNull.Value;
            return 0;
        }

        [Builtin(
            "SQLGetField",
            Doc = "1192472",
            Errors = "274 no such connection or recordset, or a row number on a connection's recordset (raised quietly); 294 no current row (raised quietly); 266 no such field (raised quietly)",
            Notes = "A field by its name (without case), else by 'Field<n>' (1 = the first). NULL and binary data give \"\"; a date and time takes [SQL]DefaultTimestampFormat (0 to 13: the .NET formats t T d D g G f F m y r s u U; unset: 11); the text is cut to [SQL]TextColWidth (255). Options sql 'off': \"\" and 269, counted as not simulated."
        )]
        private static Value SQLGetField(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLGetField", a, Outside.SqlText(h, t, a));
            long hd = A.I(a.Get(0));
            string field = A.S(a.Get(1));
            long row = a.Count > 2 ? A.I(a.Get(2)) : -1;
            int e = Field(h, hd, field, row, true, out object? v, out bool known);
            if (!known)
            {
                NoHandle(h, t, hd, true);
                return W.Empty;
            }
            string text = "";
            if (e == 0)
            {
                int te = Text(v, out _, out text, TextWidth(h), DateFormat(h));
                // (a recordset of SQLGetRecordset gives the text whatever the conversion said)
                if (h.SqlData.Get<SqlConn>(hd) != null)
                    e = te;
            }
            Trace(h, t, h.SqlData.Get<SqlObject>(hd), 1, "SQLGetField(" + hd + ", " + field + ") = " + text);
            Raise(t, e, true);
            return W.S(A.Cap(text));
        }

        [Builtin(
            "SQLIsNullField",
            Doc = "1192476",
            Errors = "274 no such connection or recordset, or a row number on a connection's recordset (raised quietly); 294 no current row (raised quietly); 266 no such field (raised quietly)",
            Notes = "1 for NULL and on every error, else 0; the field by its name only. Options sql 'off': 0 and 269, counted as not simulated."
        )]
        private static Value SQLIsNullField(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLIsNullField", a, Outside.SqlCount(h, t, a));
            long hd = A.I(a.Get(0));
            long row = a.Count > 2 ? A.I(a.Get(2)) : -1;
            int e = Field(h, hd, A.S(a.Get(1)), row, false, out object? v, out bool known);
            if (!known)
            {
                NoHandle(h, t, hd, true);
                return W.I(1);
            }
            if (e != 0)
            {
                Raise(t, e, true);
                return W.I(1);
            }
            return W.I(v == null || v is DBNull ? 1 : 0);
        }

        private static bool Columns(
            SimHost h,
            long handle,
            out List<string> names,
            out List<Type?> types,
            out List<int> sizes,
            out int error
        )
        {
            names = new List<string>();
            types = new List<Type?>();
            sizes = new List<int>();
            error = 0;
            var c = h.SqlData.Get<SqlConn>(handle);
            if (c != null)
            {
                names = c.ColNames;
                types = c.ColTypes;
                sizes = c.ColSizes;
                return true;
            }
            var rs = h.SqlData.Get<SqlRecordset>(handle);
            if (rs == null)
                return false;
            if (rs.Table == null)
            {
                error = SqlErr.Software;
                return true;
            }
            foreach (DataColumn col in rs.Table.Columns)
            {
                names.Add(col.ColumnName);
                types.Add(col.DataType);
                sizes.Add(col.MaxLength);
            }
            return true;
        }

        [Builtin(
            "SQLNumFields",
            Doc = "1192480",
            Errors = "274 no such connection or recordset (raised quietly)",
            Notes = "The number of columns of the connection's recordset or of a recordset of SQLGetRecordset. Options sql 'off': 0 and 269, counted as not simulated."
        )]
        private static Value SQLNumFields(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLNumFields", a, Outside.SqlCount(h, t, a));
            long hd = A.I(a.Get(0));
            if (!Columns(h, hd, out var names, out _, out _, out int e))
            {
                NoHandle(h, t, hd, true);
                return W.I(0);
            }
            Raise(t, e, true);
            return W.I(names.Count);
        }

        [Builtin(
            "SQLFieldInfo",
            Doc = "1192471",
            Errors = "274 no such connection or recordset (raised quietly, returned); 266 no such field (raised quietly, returned)",
            Effects = "writes the field's name and width into sName and nWidth",
            Notes = "hField 0 = the first column; a column without a name is 'Field<n>'. The width is the one a format needs: text its size up to [SQL]TextColWidth, a number or date by its type. Options sql 'off': \"\" and 0 written, 0 returned, 269, counted as not simulated."
        )]
        private static Value SQLFieldInfo(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLFieldInfo", a, Outside.SQLFieldInfoOff(h, t, a));
            long hd = A.I(a.Get(0));
            long field = A.I(a.Get(1));
            W.Out(t, a.Get(2), "");
            W.Out(t, a.Get(3), 0);
            if (!Columns(h, hd, out var names, out var types, out var sizes, out int e))
            {
                NoHandle(h, t, hd, true);
                return W.I(Codes.InvalidArg);
            }
            if (e == 0 && (field < 0 || field >= names.Count))
                e = SqlErr.FieldNotFound;
            if (e != 0)
                return W.I(Raise(t, e, true));
            int i = (int)field;
            string name = names[i];
            if (string.IsNullOrWhiteSpace(name))
                name = "Field" + (i + 1).ToString(CultureInfo.InvariantCulture);
            W.Out(t, a.Get(2), A.Cap(name));
            W.Out(t, a.Get(3), Width(types[i], sizes[i], TextWidth(h)));
            return W.I(0);
        }

        [Builtin(
            "SQLRowCount",
            Doc = "1192490",
            Errors = "274 no such connection or recordset (raised quietly)",
            Notes = "The rows of a recordset of SQLGetRecordset; -1 for a connection's own recordset (it is read as it goes) and on an error. Options sql 'off': 0 and 269, counted as not simulated."
        )]
        private static Value SQLRowCount(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLRowCount", a, Outside.SqlCount(h, t, a));
            long hd = A.I(a.Get(0));
            if (h.SqlData.Get<SqlConn>(hd) != null)
                return W.I(-1);
            var rs = h.SqlData.Get<SqlRecordset>(hd);
            if (rs == null)
            {
                NoHandle(h, t, hd, true);
                return W.I(-1);
            }
            if (rs.Table == null)
            {
                Raise(t, SqlErr.Software, true);
                return W.I(-1);
            }
            return W.I(rs.Table.Rows.Count);
        }

        [Builtin(
            "SQLNumChange",
            Doc = "1192479",
            Errors = "274 no such connection (raised quietly)",
            Notes = "The rows the connection's last statement changed. Options sql 'off': 0 and 269, counted as not simulated."
        )]
        private static Value SQLNumChange(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLNumChange", a, Outside.SqlCount(h, t, a));
            long hd = A.I(a.Get(0));
            var c = h.SqlData.Get<SqlConn>(hd);
            if (c == null)
            {
                NoHandle(h, t, hd, true);
                return W.I(0);
            }
            return W.I(c.Affected);
        }

        // ------------------------------------------------------------------ information
        private static string DefaultInfo(long type)
        {
            switch (type)
            {
                case 3:
                case 4:
                case 6:
                    return "-1";
                case 5:
                    return ConnectionState.Closed.ToString();
                case 7:
                    return "0";
                default:
                    return "";
            }
        }

        [Builtin(
            "SQLInfo",
            Doc = "1192475",
            Errors = "274 no such handle, or a type the object does not have (raised quietly)",
            Notes = "Of a connection: 0 the connection string, 1 the statement of the last SQLExec, 2 the device table, 5 the connection's state (Closed, Open ...), 7 \"0\" while a transaction is pending, else \"1\"; of a recordset of SQLGetRecordset: 0, 1 its statement, 6 its connection's handle; of a query: 1, 6. Options sql 'off': \"\" and 269, counted as not simulated."
        )]
        private static Value SQLInfo(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLInfo", a, Outside.SqlText(h, t, a));
            long hd = A.I(a.Get(0));
            long type = A.Short(A.I(a.Get(1)));
            int e = 0;
            string? text = null;
            switch (h.SqlData.Get<SqlObject>(hd))
            {
                case SqlConn c:
                    switch (type)
                    {
                        case 0:
                            text = c.ConnString;
                            break;
                        case 1:
                            text = c.LegacySelect;
                            break;
                        case 2:
                            text = "";
                            break;
                        case 5:
                            text = c.State.ToString();
                            break;
                        case 7:
                            text = c.TranPending ? "0" : "1";
                            break;
                    }
                    break;
                case SqlRecordset rs:
                    switch (type)
                    {
                        case 0:
                            text = rs.ConnString;
                            break;
                        case 1:
                            text = rs.Statement;
                            break;
                        case 6:
                            text = rs.Conn.ToString(CultureInfo.InvariantCulture);
                            break;
                    }
                    break;
                case SqlQuery q:
                    switch (type)
                    {
                        case 1:
                            text = q.Statement;
                            break;
                        case 6:
                            text = q.Conn.ToString(CultureInfo.InvariantCulture);
                            break;
                    }
                    break;
            }
            if (text == null)
            {
                text = DefaultInfo(type);
                e = SqlErr.WrongArgument;
            }
            Raise(t, e, true);
            return W.S(A.Cap(text));
        }

        [Builtin(
            "SQLErrMsg",
            Doc = "1192469",
            Notes = "The last SQL error, of a handle or of all (-1): 'SQL Error (<data error> - <its text>) on Handle (<handle>). <the provider's message>.', cut to 253 characters; \"Successful completion\" before any error. Nothing clears it: it stays after later calls that succeed. A failed statement's message is the provider exception's, a '.', then the provider's error list ('Odbc Info: ' and a line 'Index #n: Error: ...' each). The errors of a connection's own recordset (SQLNext, SQLEnd, SQLPrev) are the manager's, not the connection's: SQLErrMsg(-1) shows them, SQLErrMsg(hSQL) does not. Stepping past the last row of a result records 'SQL Error (14 - End of data for recordset) on Handle (-1)'; SQLNext on a result without rows records data error 18 ('Merging problem for connected data'). Options sql 'off': \"SQL Error\" after a failed call (1100), counted as not simulated."
        )]
        private static Value SQLErrMsg(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLErrMsg", a, Outside.SQLErrMsgOff(h, t, a));
            long hd = a.Count > 0 ? A.I(a.Get(0)) : -1;
            var info = (hd == -1 ? null : h.SqlData.Get<SqlObject>(hd))?.Last ?? h.SqlData.Last;
            if (info.Error == 0)
                return W.S("Successful completion");
            string m = info.Message();
            if (m.Length > ErrMsgMax)
                m = m.Substring(0, ErrMsgMax);
            return W.S(m.Length > 0 ? m : "SQL Error");
        }

        // the message goes into a buffer of 0xfe bytes
        private const int ErrMsgMax = 253;

        private static Value TraceSet(SimHost h, CicodeTask t, long handle, int level)
        {
            int e = 0;
            if (handle != -1)
            {
                var o = h.SqlData.Get<SqlObject>(handle);
                if (o == null)
                    e = SqlErr.WrongArgument;
                else
                    o.TraceLevel = level;
            }
            else
            {
                h.SqlData.TraceLevel = level;
                foreach (var o in h.SqlData.Objects.Values)
                    o.TraceLevel = level;
            }
            if (e != 0)
                SetError(h, null, -1, e, "General");
            return W.I(Raise(t, e, true));
        }

        [Builtin(
            "SQLTraceOn",
            Doc = "1192493",
            Errors = "274 no such handle, or a level other than 0 and 1 (raised quietly, returned)",
            Notes = "The SQL calls of a handle, or of all (-1), go to the simulator's log (level 1: with the field values); no trace file is written. Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLTraceOn(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLTraceOn", a, Outside.SqlStatus(h, t, a));
            long hd = a.Count > 1 ? A.I(a.Get(1)) : -1;
            long level = a.Count > 2 ? A.Short(A.I(a.Get(2))) : 0;
            if (level < 0 || level > 1)
            {
                SetError(h, null, -1, SqlErr.WrongArgument, "Wrong level of tracing");
                return W.I(Raise(t, SqlErr.WrongArgument, true));
            }
            return TraceSet(h, t, hd, (int)level);
        }

        [Builtin(
            "SQLTraceOff",
            Doc = "1192492",
            Errors = "274 no such handle (raised quietly, returned)",
            Notes = "Options sql 'off': 269, counted as not simulated."
        )]
        private static Value SQLTraceOff(SimHost h, CicodeTask t, ArgList a)
        {
            if (Off(h))
                return Stub(h, t, "SQLTraceOff", a, Outside.SqlStatus(h, t, a));
            return TraceSet(h, t, a.Count > 0 ? A.I(a.Get(0)) : -1, -1);
        }
    }
}
