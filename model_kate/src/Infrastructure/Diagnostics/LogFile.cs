using System;
using System.Collections.Generic;
using System.IO;

namespace model_kate.Infrastructure.Diagnostics
{
    public static class LogFile
    {
        public static string PrimaryPath => Path.Combine(Directory.GetCurrentDirectory(), "erro_kate.txt");

        public static void AppendLine(string message)
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                PrimaryPath,
                Path.Combine(AppContext.BaseDirectory, "erro_kate.txt")
            };

            foreach (var target in targets)
            {
                try
                {
                    var directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.AppendAllText(target, message + Environment.NewLine);
                }
                catch
                {
                }
            }
        }
    }
}