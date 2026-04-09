using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace Emutastic.Views
{
    public partial class BiosRequiredWindow : Window
    {
        public BiosRequiredWindow(string consoleName, List<string> biosFiles,
            string region = "Unknown")
        {
            InitializeComponent();

            string systemDir = AppPaths.GetFolder("System");

            string regionClause = region is "Unknown" or "World"
                ? ""
                : $" ({region} region)";

            string fileWord = biosFiles.Count == 1 ? "file" : "one of the following files";

            MessageText.Text =
                $"{consoleName}{regionClause} requires a BIOS {fileWord} to run. "
              + "Place it in the system directory:";

            BiosFilesText.Text = string.Join("\n", biosFiles);

            SystemDirText.Text = systemDir;
        }

        private void OK_Click(object sender, RoutedEventArgs e) => Close();
    }
}
