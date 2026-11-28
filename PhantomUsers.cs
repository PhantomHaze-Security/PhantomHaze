using System.Security.Cryptography;
using System.Text.Json;

namespace PhantomHaze;

internal sealed record PhantomUser(Guid Id, string Name, string? PinHash = null)
{
    public static bool IsPin(string pin) => pin.Length == 4 && pin.All(char.IsAsciiDigit);

    public PhantomUser WithPin(string pin)
    {
        if (!IsPin(pin)) throw new ArgumentException("Enter exactly four digits.");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return this with { PinHash = Convert.ToBase64String([.. salt, .. Derive(pin, salt)]) };
    }

    public bool Accepts(string pin)
    {
        if (PinHash is null) return true;
        if (!IsPin(pin)) return false;
        Span<byte> stored = stackalloc byte[48];
        return Convert.TryFromBase64String(PinHash, stored, out int count) && count == 48 && CryptographicOperations.FixedTimeEquals(stored[16..], Derive(pin, stored[..16]));
    }

    private static byte[] Derive(string pin, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(pin, salt, 100_000, HashAlgorithmName.SHA256, 32);
}

internal sealed record PhantomUsers(bool Enabled, Guid DefaultId, List<PhantomUser> Users, List<Guid> Retired, long Revision)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public PhantomUser Default => Users.Single(user => user.Id == DefaultId);
}

// Guid.Empty owns the original installation's files in place. All later profiles use immutable directory names.
internal sealed class PhantomUserStore
{
    private readonly string _root;
    private readonly string _profiles;
    private readonly string _manifest;
    public string Root => _root;

    public PhantomUserStore(string root)
    {
        _root = root;
        _profiles = Path.Combine(root, "profiles");
        _manifest = Path.Combine(_profiles, "users.json");
        Directory.CreateDirectory(_profiles);
    }

    public string DirectoryFor(Guid id) => id == Guid.Empty ? _root : Path.Combine(_profiles, id.ToString("N"));

    public (PhantomUsers State, PhantomUser User, FileStream Lease) Open(PhantomUser? requested)
    {
        using var gate = LockManifest();
        PhantomUsers state = CleanRetired(Read());
        PhantomUser user = state.Users.FirstOrDefault(u => u.Id == requested?.Id && u.PinHash == requested.PinHash) ?? state.Default;
        Directory.CreateDirectory(DirectoryFor(user.Id));
        // Multiple windows may use a profile. Its data can be removed only after its last window has finished saving.
        string path = Path.Combine(_profiles, $"{user.Id:N}.{Guid.NewGuid():N}.session");
        var lease = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        return (state, user, lease);
    }

    private FileStream LockManifest()
    {
        string path = Path.Combine(_profiles, "users.lock");
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 32 && attempt < 40) { Thread.Sleep(25); }
        }
    }

    public PhantomUsers Read()
    {
        if (!File.Exists(_manifest))
            return new(false, Guid.Empty, [new(Guid.Empty, "Master")], [], 0);
        using var stream = new FileStream(_manifest, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var state = JsonSerializer.Deserialize<PhantomUsers>(stream) ?? throw new InvalidDataException("The user list could not be read.");
        Validate(state);
        return state;
    }

    private static void Validate(PhantomUsers state)
    {
        if (state.Users is null || state.Retired is null || state.Users.Count == 0 ||
            state.Users.Any(u => u is null || string.IsNullOrWhiteSpace(u.Name)) ||
            state.Users.Select(u => u.Id).Distinct().Count() != state.Users.Count ||
            !state.Users.Any(u => u.Id == state.DefaultId && u.PinHash is null) ||
            state.Users.Any(u => state.Retired.Contains(u.Id)) || (!state.Enabled && state.Users.Count != 1))
            throw new InvalidDataException("The PhantomHaze user list is invalid.");
    }

    public PhantomUsers Save(PhantomUsers proposed, FileStream? currentWindow = null)
    {
        using var gate = LockManifest();
        PhantomUsers previous = Read();
        if (previous.Revision != proposed.Revision)
            throw new IOException("Another window changed the user list. Reopen Users and try again.");
        var removed = previous.Users.Where(u => proposed.Users.All(kept => kept.Id != u.Id)).ToList();
        foreach (PhantomUser user in removed)
        {
            if (ProfileInUse(user.Id, currentWindow))
                throw new IOException($"Close the other PhantomHaze window using {user.Name} before removing this user.");
        }
        var next = proposed with
        {
            Revision = previous.Revision + 1,
            Retired = previous.Retired.Concat(removed.Select(u => u.Id)).Distinct().ToList(),
        };
        Validate(next);
        Write(next);
        return CleanRetired(next);
    }

    private void Write(PhantomUsers state)
    {
        string temp = _manifest + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, _manifest, overwrite: true);
    }

    public void FinishSession()
    {
        using var gate = LockManifest();
        CleanRetired(Read());
    }

    private PhantomUsers CleanRetired(PhantomUsers state)
    {
        var remaining = new List<Guid>();
        foreach (Guid id in state.Retired)
        {
            try
            {
                if (ProfileInUse(id, null))
                {
                    remaining.Add(id);
                    continue;
                }
                if (id == Guid.Empty) DeleteLegacyData();
                else if (Directory.Exists(DirectoryFor(id))) Directory.Delete(DirectoryFor(id), recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remaining.Add(id);
            }
        }
        if (remaining.Count == state.Retired.Count) return state;
        state = state with { Retired = remaining };
        Write(state);
        return state;
    }

    private bool ProfileInUse(Guid id, FileStream? currentWindow)
    {
        foreach (string path in Directory.EnumerateFiles(_profiles, $"{id:N}.*.session"))
        {
            if (string.Equals(currentWindow?.Name, path, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(path); } // An abandoned marker can be removed. A live window holds it exclusively.
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
        return false;
    }

    private void DeleteLegacyData()
    {
        foreach (string name in new[] { "settings.json", "favorites.json", "history.json", "cookies.dat", "update-reminder.json" })
        {
            File.Delete(Path.Combine(_root, name));
            foreach (string backup in Directory.EnumerateFiles(_root, name + ".unreadable-*")) File.Delete(backup);
        }
        foreach (string name in new[] { "extensions", "fonts" })
        {
            string directory = Path.Combine(_root, name);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
