using System;
using System.Reflection;

namespace Win11Toolbox.Services;

/// <summary>版本号只在 csproj 里写一次，界面各处读这里，避免两处对不上。</summary>
internal static class AppInfo
{
    public static string Version
    {
        get
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    // InformationalVersion 可能带 "+<commit>" 后缀，界面上不要
                    int plus = info.IndexOf('+');
                    return plus > 0 ? info[..plus] : info;
                }

                var v = asm.GetName().Version;
                return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch
            {
                return "1.0.0";
            }
        }
    }
}
