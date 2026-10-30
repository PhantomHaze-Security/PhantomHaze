using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace PhantomHaze;

internal sealed class AbTutorConnectionsForm : Form
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint TcpTableOwnerPidAll = 5;
    private const uint UdpTableOwnerPid = 1;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorNoData = 232;

    private readonly Label _summaryLabel = new();
    private readonly Label _updatedLabel = new();
    private readonly DataGridView _connectionGrid = new();
    private readonly Button _refreshButton = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2000 };

    public AbTutorConnectionsForm(bool darkTheme)
    {
        Text = "AB Tutor Network Connections";
        Width = 820;
        Height = 460;
        MinimumSize = new Size(640, 340);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = darkTheme ? Color.FromArgb(31, 36, 43) : Color.FromArgb(246, 249, 253);
        ForeColor = darkTheme ? Color.FromArgb(224, 233, 245) : Color.FromArgb(25, 25, 25);

        BuildUi(darkTheme);
        _refreshButton.Click += (_, _) => RefreshConnections();
        _refreshTimer.Tick += (_, _) => RefreshConnections();
        FormClosed += (_, _) => _refreshTimer.Dispose();

        RefreshConnections();
        _refreshTimer.Start();
    }

    private void BuildUi(bool darkTheme)
    {
        Color panelColor = darkTheme ? Color.FromArgb(40, 45, 53) : Color.FromArgb(243, 247, 252);
        Color inputColor = darkTheme ? Color.FromArgb(54, 62, 74) : Color.White;
        Color textColor = darkTheme ? Color.FromArgb(224, 233, 245) : Color.FromArgb(25, 25, 25);
        Color borderColor = darkTheme ? Color.FromArgb(105, 116, 132) : Color.FromArgb(158, 177, 201);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.Padding = new Padding(10, 4, 10, 4);
        _summaryLabel.BackColor = panelColor;
        _summaryLabel.ForeColor = textColor;

        _connectionGrid.Dock = DockStyle.Fill;
        _connectionGrid.ReadOnly = true;
        _connectionGrid.AllowUserToAddRows = false;
        _connectionGrid.AllowUserToDeleteRows = false;
        _connectionGrid.AllowUserToResizeRows = false;
        _connectionGrid.RowHeadersVisible = false;
        _connectionGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _connectionGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _connectionGrid.MultiSelect = false;
        _connectionGrid.BackgroundColor = inputColor;
        _connectionGrid.GridColor = borderColor;
        _connectionGrid.DefaultCellStyle.BackColor = inputColor;
        _connectionGrid.DefaultCellStyle.ForeColor = textColor;
        _connectionGrid.DefaultCellStyle.SelectionBackColor = darkTheme
            ? Color.FromArgb(76, 91, 110)
            : Color.FromArgb(202, 220, 240);
        _connectionGrid.DefaultCellStyle.SelectionForeColor = textColor;
        _connectionGrid.ColumnHeadersDefaultCellStyle.BackColor = panelColor;
        _connectionGrid.ColumnHeadersDefaultCellStyle.ForeColor = textColor;
        _connectionGrid.EnableHeadersVisualStyles = false;
        _connectionGrid.Columns.Add("Protocol", "Protocol");
        _connectionGrid.Columns.Add("State", "State");
        _connectionGrid.Columns.Add("LocalEndpoint", "Local endpoint");
        _connectionGrid.Columns.Add("RemoteEndpoint", "Remote endpoint");
        _connectionGrid.Columns[0].FillWeight = 18;
        _connectionGrid.Columns[1].FillWeight = 20;
        _connectionGrid.Columns[2].FillWeight = 30;
        _connectionGrid.Columns[3].FillWeight = 32;

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _updatedLabel.Dock = DockStyle.Fill;
        _updatedLabel.TextAlign = ContentAlignment.MiddleLeft;
        _updatedLabel.ForeColor = textColor;

        _refreshButton.Text = "Refresh";
        _refreshButton.AutoSize = true;
        _refreshButton.MinimumSize = new Size(90, 30);
        _refreshButton.FlatStyle = FlatStyle.Flat;
        _refreshButton.BackColor = panelColor;
        _refreshButton.ForeColor = textColor;
        _refreshButton.FlatAppearance.BorderColor = borderColor;

        footer.Controls.Add(_updatedLabel, 0, 0);
        footer.Controls.Add(_refreshButton, 1, 0);
        layout.Controls.Add(_summaryLabel, 0, 0);
        layout.Controls.Add(_connectionGrid, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
    }

    private void RefreshConnections()
    {
        try
        {
            HashSet<uint> processIds = FindAbTutorProcessIds();
            List<ConnectionEntry> entries = processIds.Count == 0
                ? new List<ConnectionEntry>()
                : ReadConnections(processIds);

            _connectionGrid.Rows.Clear();
            foreach (ConnectionEntry entry in entries)
            {
                _connectionGrid.Rows.Add(entry.Protocol, entry.State, entry.LocalEndpoint, entry.RemoteEndpoint);
            }

            if (processIds.Count == 0)
            {
                _summaryLabel.Text = "AB Tutor client was not found. Connection presence does not indicate screen monitoring.";
            }
            else
            {
                int tcpCount = entries.Count(entry => entry.Protocol.StartsWith("TCP", StringComparison.Ordinal));
                int udpCount = entries.Count - tcpCount;
                string processText = string.Join(", ", processIds.OrderBy(id => id).Select(id => $"PID {id}"));
                _summaryLabel.Text = $"AB Tutor found ({processText})  |  {tcpCount} TCP connections  |  {udpCount} UDP endpoints\r\n" +
                    "Endpoint presence only: idle connections carry no traffic, and traffic does not prove screen monitoring.";
            }

            _updatedLabel.Text = $"Updated {DateTime.Now:T}  |  Refreshes every 2 seconds";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ExternalException)
        {
            _summaryLabel.Text = $"Could not read connection data: {exception.Message}";
            _updatedLabel.Text = $"Update failed at {DateTime.Now:T}";
            _connectionGrid.Rows.Clear();
        }
    }

    private static HashSet<uint> FindAbTutorProcessIds()
    {
        var processIds = new HashSet<uint>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string normalizedName = new(process.ProcessName
                        .Where(char.IsLetterOrDigit)
                        .ToArray());
                    if (normalizedName.Equals("abtutorclient", StringComparison.OrdinalIgnoreCase))
                    {
                        processIds.Add((uint)process.Id);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }
            }
        }

        return processIds;
    }

    private static List<ConnectionEntry> ReadConnections(HashSet<uint> processIds)
    {
        var entries = new List<ConnectionEntry>();
        AddTcp4Entries(entries, processIds);
        AddTcp6Entries(entries, processIds);
        AddUdp4Entries(entries, processIds);
        AddUdp6Entries(entries, processIds);
        return entries
            .OrderBy(entry => entry.Protocol, StringComparer.Ordinal)
            .ThenBy(entry => entry.RemoteEndpoint, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddTcp4Entries(List<ConnectionEntry> entries, HashSet<uint> processIds)
    {
        foreach (MibTcpRowOwnerPid row in ReadTable<MibTcpRowOwnerPid>(true, AfInet, TcpTableOwnerPidAll))
        {
            if (processIds.Contains(row.OwningPid) && IsActiveTcpState(row.State))
            {
                entries.Add(new ConnectionEntry(
                    "TCPv4",
                    DescribeTcpState(row.State),
                    FormatEndpoint(row.LocalAddress, row.LocalPort),
                    FormatEndpoint(row.RemoteAddress, row.RemotePort)));
            }
        }
    }

    private static void AddTcp6Entries(List<ConnectionEntry> entries, HashSet<uint> processIds)
    {
        foreach (MibTcp6RowOwnerPid row in ReadTable<MibTcp6RowOwnerPid>(true, AfInet6, TcpTableOwnerPidAll))
        {
            if (processIds.Contains(row.OwningPid) && IsActiveTcpState(row.State))
            {
                entries.Add(new ConnectionEntry(
                    "TCPv6",
                    DescribeTcpState(row.State),
                    FormatEndpoint(new IPAddress(row.LocalAddress, row.LocalScopeId), row.LocalPort),
                    FormatEndpoint(new IPAddress(row.RemoteAddress, row.RemoteScopeId), row.RemotePort)));
            }
        }
    }

    private static void AddUdp4Entries(List<ConnectionEntry> entries, HashSet<uint> processIds)
    {
        foreach (MibUdpRowOwnerPid row in ReadTable<MibUdpRowOwnerPid>(false, AfInet, UdpTableOwnerPid))
        {
            if (processIds.Contains(row.OwningPid))
            {
                entries.Add(new ConnectionEntry(
                    "UDPv4",
                    "Bound endpoint",
                    FormatEndpoint(row.LocalAddress, row.LocalPort),
                    "Not available for UDP"));
            }
        }
    }

    private static void AddUdp6Entries(List<ConnectionEntry> entries, HashSet<uint> processIds)
    {
        foreach (MibUdp6RowOwnerPid row in ReadTable<MibUdp6RowOwnerPid>(false, AfInet6, UdpTableOwnerPid))
        {
            if (processIds.Contains(row.OwningPid))
            {
                entries.Add(new ConnectionEntry(
                    "UDPv6",
                    "Bound endpoint",
                    FormatEndpoint(new IPAddress(row.LocalAddress, row.LocalScopeId), row.LocalPort),
                    "Not available for UDP"));
            }
        }
    }

    private static T[] ReadTable<T>(bool tcp, int addressFamily, uint tableClass) where T : struct
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint size = 0;
            uint result = QueryTable(tcp, IntPtr.Zero, ref size, addressFamily, tableClass);
            if (result == ErrorNoData || (result == 0 && size == 0))
            {
                return Array.Empty<T>();
            }

            if (result != ErrorInsufficientBuffer && result != 0)
            {
                throw new Win32Exception((int)result);
            }

            if (size == 0)
            {
                return Array.Empty<T>();
            }

            IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                result = QueryTable(tcp, buffer, ref size, addressFamily, tableClass);
                if (result == ErrorInsufficientBuffer)
                {
                    continue;
                }

                if (result == ErrorNoData)
                {
                    return Array.Empty<T>();
                }

                if (result != 0)
                {
                    throw new Win32Exception((int)result);
                }

                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf<T>();
                if (count < 0 || 4L + (long)count * rowSize > size)
                {
                    throw new InvalidDataException("Windows returned an invalid connection table size.");
                }

                var rows = new T[count];
                for (int index = 0; index < count; index++)
                {
                    IntPtr rowAddress = IntPtr.Add(buffer, 4 + index * rowSize);
                    rows[index] = Marshal.PtrToStructure<T>(rowAddress);
                }

                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new Win32Exception((int)ErrorInsufficientBuffer);
    }

    private static uint QueryTable(bool tcp, IntPtr table, ref uint size, int addressFamily, uint tableClass)
    {
        return tcp
            ? GetExtendedTcpTable(table, ref size, false, addressFamily, tableClass, 0)
            : GetExtendedUdpTable(table, ref size, false, addressFamily, tableClass, 0);
    }

    private static bool IsActiveTcpState(uint state) => state is >= 3 and <= 10 or 12;

    private static string DescribeTcpState(uint state) => state switch
    {
        3 => "Connecting",
        4 => "Incoming",
        5 => "Established",
        6 => "Fin wait 1",
        7 => "Fin wait 2",
        8 => "Close wait",
        9 => "Closing",
        10 => "Last ack",
        12 => "Deleting",
        _ => $"State {state}",
    };

    private static string FormatEndpoint(uint address, uint port)
    {
        var ipAddress = new IPAddress(BitConverter.GetBytes(address));
        return FormatEndpoint(ipAddress, port);
    }

    private static string FormatEndpoint(IPAddress address, uint networkPort)
    {
        ushort port = unchecked((ushort)IPAddress.NetworkToHostOrder(unchecked((short)(networkPort & 0xFFFF))));
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]:{port}"
            : $"{address}:{port}";
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        uint tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedUdpTable(
        IntPtr udpTable,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        uint tableClass,
        uint reserved);

    private sealed record ConnectionEntry(string Protocol, string State, string LocalEndpoint, string RemoteEndpoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddress;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16, ArraySubType = UnmanagedType.U1)]
        public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16, ArraySubType = UnmanagedType.U1)]
        public byte[] RemoteAddress;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16, ArraySubType = UnmanagedType.U1)]
        public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }
}