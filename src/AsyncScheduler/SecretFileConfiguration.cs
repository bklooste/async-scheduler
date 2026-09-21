namespace AsyncScheduler;

/// <summary>
/// Docker/k8s secret support: for any env var <c>X__Y_FILE=/path</c>, sets <c>X:Y</c> to the file's
/// contents. Lets secrets be mounted instead of passed as plain env vars (which show in
/// <c>docker inspect</c>). A <c>_FILE</c> value overrides the plain one.
/// </summary>
public static class SecretFileConfiguration
{
    public static IConfigurationBuilder AddSecretFiles(this IConfigurationBuilder builder)
    {
        var values = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var name = (string)e.Key;
            if (!name.EndsWith("_FILE", StringComparison.Ordinal) || e.Value is not string path) continue;
            if (!File.Exists(path)) throw new FileNotFoundException($"{name} points at a missing file", path);
            values[name[..^"_FILE".Length].Replace("__", ":")] = File.ReadAllText(path).Trim();
        }
        return builder.AddInMemoryCollection(values);
    }
}
