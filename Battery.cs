using System.Runtime.InteropServices;

namespace BudsTray;

/// Reads the battery level Windows receives over Hands-Free (HFP). It lives on the
/// "Hands-Free AG" device node: BTHENUM\{0000111E-...}_...\..._<mac>_C00000000.
static class Battery
{
    const string HandsFreeUuid = "{0000111E-0000-1000-8000-00805F9B34FB}";
    static readonly DevPropKey DEVPKEY_Bluetooth_Battery = new() { Fmtid = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), Pid = 2 };

    public static int? Read(string mac)
    {
        string? instanceId = FindHandsFreeNode(mac);
        if (instanceId is null || CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0) return null;

        var key = DEVPKEY_Bluetooth_Battery;
        uint size = 1;
        byte value = 0;
        int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, ref value, ref size, 0);
        return cr == 0 && type == DEVPROP_TYPE_BYTE ? value : null;
    }

    static string? FindHandsFreeNode(string mac)
    {
        if (CM_Get_Device_ID_List_SizeW(out uint len, "BTHENUM", CM_GETIDLIST_FILTER_ENUMERATOR) != 0) return null;
        var buffer = new char[len];
        if (CM_Get_Device_ID_ListW("BTHENUM", buffer, len, CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT) != 0) return null;
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(id => id.Contains(HandsFreeUuid, StringComparison.OrdinalIgnoreCase)
                && id.Contains(mac, StringComparison.OrdinalIgnoreCase));
    }

    const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
    const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    const uint DEVPROP_TYPE_BYTE = 0x3;

    [StructLayout(LayoutKind.Sequential)]
    struct DevPropKey { public Guid Fmtid; public uint Pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, ref byte buffer, ref uint size, uint flags);
}
