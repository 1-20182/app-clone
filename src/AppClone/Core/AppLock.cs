using System.Security.Cryptography;
using System.Text;

namespace AppClone.Core;

/// <summary>
/// 启动密码锁（P1）：PBKDF2-SHA256 存哈希，settings.json 里不出现明文。
/// ponytail: 只防「顺手打开」，settings.json 被拷走后仍可离线爆破 ——
/// 升级路径 = DPAPI(CurrentUser) 加密哈希串，或改存 Windows 凭据管理器。
/// </summary>
public static class AppLock
{
    private const int Iterations = 100_000;

    /// <summary>是否已启用启动锁（PasswordHash 非空即启用）。</summary>
    public static bool Enabled => !string.IsNullOrEmpty(Settings.Current.PasswordHash);

    /// <summary>设置 / 修改密码；传空串表示关闭启动锁。</summary>
    public static void Set(string password)
    {
        if (password.Length == 0)
        {
            Settings.Current.PasswordHash = "";
        }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            Settings.Current.PasswordHash =
                Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(Hash(password, salt));
        }
        Settings.Current.Save();
    }

    /// <summary>校验密码；未启用时恒为 true。恒定时间比较，避免时序侧信道。</summary>
    public static bool Verify(string password)
    {
        var stored = Settings.Current.PasswordHash;
        if (string.IsNullOrEmpty(stored)) return true;
        var parts = stored.Split(':');
        if (parts.Length != 2) return false;   // 字段被手改坏了 → 一律拒绝
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            return CryptographicOperations.FixedTimeEquals(Hash(password, salt), expected);
        }
        catch { return false; }
    }

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
}
