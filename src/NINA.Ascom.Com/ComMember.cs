using System.Reflection;
using System.Runtime.Versioning;

namespace NINA.Ascom.Com;

/// <summary>
/// IDispatch-based property / method access for late-bound COM objects.
/// Uses <see cref="Type.InvokeMember(string, BindingFlags, Binder, object, object[])"/>
/// which on a COM <c>System.__ComObject</c> routes through
/// <c>IDispatch::Invoke</c>.
///
/// <para>Some ASCOM drivers (notably older focuser and filter-wheel
/// drivers built before IFocuserV3 / IFilterWheelV2 finalised their
/// dispatch IDs) don't expose every property on the default IDispatch
/// the way C# <c>dynamic</c> binds to. The DLR sees
/// <c>'System.__ComObject' does not contain a definition for 'Connected'</c>
/// and throws <see cref="Microsoft.CSharp.RuntimeBinder.RuntimeBinderException"/>.
/// Going through <c>InvokeMember</c> instead resolves the member name
/// via <c>IDispatch::GetIDsOfNames</c> + <c>IDispatch::Invoke</c>, which
/// the driver is required to implement to be ASCOM-compliant.</para>
///
/// <para>Pure helper, no state. Windows-only because the underlying
/// COM RCW infrastructure only ships there.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ComMember {
    public static T Get<T>(object target, string name) {
        var raw = target.GetType().InvokeMember(
            name, BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
            null, target, null);
        return Convert<T>(raw);
    }

    public static void Set(object target, string name, object value) {
        target.GetType().InvokeMember(
            name, BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance,
            null, target, new[] { value });
    }

    public static object? Call(object target, string name, params object[] args) {
        return target.GetType().InvokeMember(
            name, BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
            null, target, args);
    }

    /// <summary>
    /// IDispatch returns variants. <c>VT_I2</c> (short) is common for
    /// ASCOM filter-wheel Position; the caller usually wants int. Cover
    /// the conversion explicitly so call sites stay terse.
    /// </summary>
    private static T Convert<T>(object? raw) {
        if (raw is null) return default!;
        if (raw is T t) return t;
        // System.Convert handles the numeric widening cases (short → int,
        // int → double, etc) that come up with COM VARIANTs.
        return (T)System.Convert.ChangeType(raw, typeof(T))!;
    }
}
