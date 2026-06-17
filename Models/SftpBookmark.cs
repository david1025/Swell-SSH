using System;

namespace SwellSSH.Models
{
    public class SftpBookmark
    {
        public string Path { get; set; } = "/";

        public string Name { get; set; } = "/";

        public DateTime AddedAt { get; set; } = DateTime.Now;

        public string DisplayLabel => string.IsNullOrWhiteSpace(Name) || Name == "/"
            ? (Path.Length > 20 ? "…" + Path[^Math.Min(20, Path.Length)..] : Path)
            : Name;
    }
}
