using System;
using System.IO;

internal sealed class MsiDatabaseReader : IDisposable
{
    private readonly IntPtr _database;

    public MsiDatabaseReader(string path)
    {
        Check(MsiOpenDatabase(path, IntPtr.Zero, out _database));
    }

    public string Property(string name)
    {
        using var view = OpenView("SELECT `Value` FROM `Property` WHERE `Property`='" + name + "'");
        return view.FetchString() ?? throw new InvalidDataException("MSI property is missing: " + name);
    }

    public int RowCount(string table, string column)
    {
        using var view = OpenView("SELECT `" + column + "` FROM `" + table + "`");
        var count = 0;
        while (view.FetchString() is not null) count++;
        return count;
    }

    public bool Contains(string table, string column, string value)
    {
        using var view = OpenView("SELECT `" + column + "` FROM `" + table + "`");
        string? found;
        while ((found = view.FetchString()) is not null)
            if (found.Equals(value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public IReadOnlyList<string> Values(string table, string column)
    {
        var values = new List<string>();
        using var view = OpenView("SELECT `" + column + "` FROM `" + table + "`");
        string? found;
        while ((found = view.FetchString()) is not null) values.Add(found);
        return values;
    }

    public bool ContainsSubstring(string table, string column, string fragment)
    {
        using var view = OpenView("SELECT `" + column + "` FROM `" + table + "`");
        string? found;
        while ((found = view.FetchString()) is not null)
            if (found.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public IReadOnlyList<(string First, string Second)> Pairs(string table, string firstColumn, string secondColumn)
    {
        var rows = new List<(string, string)>();
        using var view = OpenView("SELECT `" + firstColumn + "`, `" + secondColumn + "` FROM `" + table + "`");
        while (view.FetchPair() is { } pair) rows.Add(pair);
        return rows;
    }

    public string Template => SummaryProperty(7);
    public string PackageCode => SummaryProperty(9);

    private string SummaryProperty(uint property)
    {
        Check(MsiGetSummaryInformation(_database, null, 0, out var summary));
        try
        {
            var text = new System.Text.StringBuilder(256);
            uint length = (uint)text.Capacity;
            var rc = MsiSummaryInfoGetProperty(summary, property, out _, out _, out _, text, ref length);
            // 234 = ERROR_MORE_DATA：属性超缓冲，按报告长度扩容重取。
            while (rc == 234)
            {
                text.Capacity = (int)(length + 1);
                length = (uint)text.Capacity;
                rc = MsiSummaryInfoGetProperty(summary, property, out _, out _, out _, text, ref length);
            }
            Check(rc);
            return text.ToString();
        }
        finally { MsiCloseHandle(summary); }
    }

    public void Dispose() => MsiCloseHandle(_database);

    private View OpenView(string sql)
    {
        Check(MsiDatabaseOpenView(_database, sql, out var handle));
        Check(MsiViewExecute(handle, IntPtr.Zero));
        return new View(handle);
    }

    private static void Check(uint code)
    {
        if (code != 0) throw new InvalidOperationException("Windows Installer database API returned " + code + ".");
    }

    private sealed class View(IntPtr handle) : IDisposable
    {
        public string? FetchString()
        {
            var result = MsiViewFetch(handle, out var record);
            if (result == 259) return null;
            Check(result);
            try
            {
                return GetRecordString(record, 1);
            }
            finally { MsiCloseHandle(record); }
        }

        private static string GetRecordString(IntPtr record, uint field)
        {
            var text = new System.Text.StringBuilder(1024);
            uint length = (uint)text.Capacity;
            var rc = MsiRecordGetString(record, field, text, ref length);
            // 234 = ERROR_MORE_DATA：字段超缓冲，按报告长度扩容重取。
            while (rc == 234)
            {
                text.Capacity = (int)(length + 1);
                length = (uint)text.Capacity;
                rc = MsiRecordGetString(record, field, text, ref length);
            }
            Check(rc);
            return text.ToString();
        }

        public (string, string)? FetchPair()
        {
            var result = MsiViewFetch(handle, out var record);
            if (result == 259) return null;
            Check(result);
            try
            {
                return (GetRecordString(record, 1), GetRecordString(record, 2));
            }
            finally { MsiCloseHandle(record); }
        }

        public void Dispose()
        {
            MsiViewClose(handle);
            MsiCloseHandle(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr database);
    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(IntPtr database, string sql, out IntPtr view);
    [System.Runtime.InteropServices.DllImport("msi.dll", EntryPoint = "MsiViewExecute")]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [System.Runtime.InteropServices.DllImport("msi.dll", EntryPoint = "MsiViewFetch")]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(IntPtr record, uint field, System.Text.StringBuilder value, ref uint length);
    [System.Runtime.InteropServices.DllImport("msi.dll", EntryPoint = "MsiViewClose")]
    private static extern uint MsiViewClose(IntPtr view);
    [System.Runtime.InteropServices.DllImport("msi.dll", EntryPoint = "MsiCloseHandle")]
    private static extern uint MsiCloseHandle(IntPtr handle);
    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MsiGetSummaryInformationW")]
    private static extern uint MsiGetSummaryInformation(IntPtr database, string? path, uint count, out IntPtr summary);
    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MsiSummaryInfoGetPropertyW")]
    private static extern uint MsiSummaryInfoGetProperty(IntPtr summary, uint property, out uint dataType,
        out int integerValue, out long fileTime, System.Text.StringBuilder value, ref uint length);
}
