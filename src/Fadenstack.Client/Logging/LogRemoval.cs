namespace Fadenstack.Logging;

/// <summary>What a removal of log files did: the files deleted and the files that stayed (held open by another process, or refused).</summary>
internal readonly record struct LogRemoval(int Removed, int Left)
{
    public static LogRemoval operator +(LogRemoval a, LogRemoval b) => new(a.Removed + b.Removed, a.Left + b.Left);

    /// <summary>Deletes the files; one that cannot be deleted is counted as left.</summary>
    internal static LogRemoval Delete(IEnumerable<FileInfo> files)
    {
        int removed = 0, left = 0;
        foreach (var file in files)
        {
            try
            {
                file.Delete();
                removed++;
            }
            catch (FileNotFoundException)
            {
                // gone already (another provider of the same app removed it)
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                left++;
            }
        }
        return new LogRemoval(removed, left);
    }

    /// <summary>Deletes every file in the folder and the folders below it (a link to a folder is not followed; the folders stay).</summary>
    internal static LogRemoval DeleteTree(DirectoryInfo folder)
    {
        var result = default(LogRemoval);
        var files = new List<FileInfo>();
        var folders = new List<DirectoryInfo>();
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo directory)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    folders.Add(directory);
                }
            }
            else if (entry is FileInfo file)
            {
                files.Add(file);
            }
        }
        result += Delete(files);
        foreach (var directory in folders)
        {
            try
            {
                result += DeleteTree(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a folder that cannot be read: what is in it stays
            }
        }
        return result;
    }
}
