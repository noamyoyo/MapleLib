using System;
using System.IO;
using System.Text;

namespace MapleLib.Helpers
{
    /// <summary>
    /// Writes a file without ever following a symbolic link at the target.
    ///
    /// File.Create / File.WriteAllText open the path itself, so a link planted at the path (or at the scratch file name
    /// next to it) sends the data to wherever the link points. Here the data goes to a new file with a random name in the
    /// same directory (<see cref="FileMode.CreateNew"/>: an existing name, link or not, is an error, never followed), and
    /// that file is then renamed over the target. A rename replaces a link itself and never writes through it, so a
    /// link planted between the check and the rename is also harmless. A link found at the target is refused.
    ///
    /// Not covered: a link in a PARENT directory of the target (it is followed; that needs openat on the directory),
    /// and the rename gives the target the default permissions and owner of a new file.
    /// A crash between the create and the rename can leave a ".&lt;guid&gt;.tmp" file behind.
    /// </summary>
    public static class NoFollowFile
    {
        /// <summary>
        /// Creates a new, empty file with a random name next to <paramref name="targetPath"/> and returns it open for
        /// reading and writing. The caller deletes it, or passes it to <see cref="Commit"/>.
        /// </summary>
        public static FileStream CreateTemp(string targetPath, out string tempPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? ".";
            tempPath = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            return new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }

        /// <summary>
        /// Moves a finished file made by <see cref="CreateTemp"/> over <paramref name="targetPath"/>. Throws
        /// <see cref="IOException"/> when the target is a symbolic link.
        /// </summary>
        public static void Commit(string tempPath, string targetPath)
        {
            string full = Path.GetFullPath(targetPath);
            RefuseSymbolicLink(full);
            File.Move(tempPath, full, overwrite: true);
        }

        /// <summary>
        /// Throws <see cref="IOException"/> when <paramref name="rootPath"/> itself, or any existing directory or file on
        /// the way from it down to <paramref name="path"/> (the last one included), is a symbolic link or other reparse
        /// point, also a dangling one. Parts that do not exist yet are fine. <paramref name="path"/> must be below
        /// <paramref name="rootPath"/>. This is a check of the state now (a link swapped in afterwards is not caught).
        /// </summary>
        public static void RefuseSymbolicLinksBelow(string rootPath, string path)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string rootWithSeparator = root + Path.DirectorySeparatorChar;
            if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Path is not below " + root + ": " + full);

            RefuseSymbolicLink(root);
            if (full.Length <= root.Length)
                return;

            string current = root;
            foreach (string segment in full.Substring(rootWithSeparator.Length).Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                var info = new FileInfo(current);
                if (info.LinkTarget != null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
                    throw new IOException("Refusing to write through the symbolic link " + current);
                if (!info.Exists && !Directory.Exists(current))
                    break;
            }
        }

        /// <summary>Throws <see cref="IOException"/> when <paramref name="path"/> is a symbolic link (also a dangling one).</summary>
        public static void RefuseSymbolicLink(string path)
        {
            if (new FileInfo(path).LinkTarget != null)
                throw new IOException("Refusing to write through the symbolic link " + Path.GetFullPath(path));
        }

        /// <summary>
        /// Lets <paramref name="write"/> fill a new file and replaces <paramref name="path"/> with it. The target is
        /// not touched when <paramref name="write"/> throws; the new file is removed then.
        /// </summary>
        public static void Write(string path, Action<FileStream> write)
        {
            string full = Path.GetFullPath(path);
            RefuseSymbolicLink(full);

            FileStream stream = CreateTemp(full, out string temp);
            try
            {
                using (stream)
                    write(stream);
                Commit(temp, full);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }

        public static void WriteAllText(string path, string contents)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(contents ?? string.Empty);
            WriteAllBytes(path, bytes);
        }

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            Write(path, stream => stream.Write(bytes, 0, bytes.Length));
        }
    }
}
