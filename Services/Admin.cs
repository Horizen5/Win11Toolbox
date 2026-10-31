// ================================================================
//  Admin.cs —— 提权判断
//  单独放一个文件，是为了让「简洁版」也能只带上这几行：
//  EasySettingsCore 需要它，但不该为了一个 IsAdmin 把整个右键菜单模块拖进来。
// ================================================================
using System.Security.Principal;

namespace Win11Toolbox
{
    internal static class Admin
    {
        /// <summary>当前进程是否以管理员身份运行。</summary>
        public static bool IsAdmin()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
