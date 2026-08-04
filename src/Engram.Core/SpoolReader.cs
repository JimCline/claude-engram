namespace Engram.Core;

public static class SpoolReader
{
    public static IReadOnlyList<string> Drain(string queueDir)
    {
        if (!Directory.Exists(queueDir))
        {
            return [];
        }

        var files = Directory.GetFiles(queueDir, "*.spool");
        Array.Sort(files, StringComparer.Ordinal);

        var contents = new List<string>(files.Length);
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            contents.Add(text);

            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return contents;
    }
}
