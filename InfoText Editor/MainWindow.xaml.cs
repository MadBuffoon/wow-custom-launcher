using Microsoft.Win32;
using System.IO;
using System.Windows;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Xml;
using System.Reflection;

namespace HtmlEditorApp
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            LoadHtmlSyntaxHighlighting();
        }

        private void LoadHtmlSyntaxHighlighting()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "HtmlEditorApp.HtmlSyntaxHighlighting.xshd"; // Adjust namespace if necessary

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            using (XmlReader reader = new XmlTextReader(stream))
            {
                htmlEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
        }

        private void OnLoadHtml(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "HTML files (*.html)|*.html|All files (*.*)|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                htmlEditor.Text = File.ReadAllText(openFileDialog.FileName);
            }
        }

        private void OnSaveHtml(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Filter = "HTML files (*.html)|*.html|All files (*.*)|*.*"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                File.WriteAllText(saveFileDialog.FileName, htmlEditor.Text);
            }
        }
    }
}