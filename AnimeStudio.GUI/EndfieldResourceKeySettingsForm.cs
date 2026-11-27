using System;
using System.Drawing;
using System.Windows.Forms;

namespace AnimeStudio.GUI
{
    internal sealed class EndfieldResourceKeySettingsForm : Form
    {
        private readonly TextBox appCodeTextBox = new();
        private readonly TextBox platformTextBox = new();
        private readonly TextBox gameVersionTextBox = new();
        private readonly TextBox packageVersionTextBox = new();
        private readonly TextBox randStrTextBox = new();

        public bool RefreshRequested { get; private set; }
        public string AppCode => appCodeTextBox.Text.Trim();
        public string Platform => platformTextBox.Text.Trim();
        public string GameVersion => gameVersionTextBox.Text.Trim();
        public string PackageVersion => packageVersionTextBox.Text.Trim();
        public string RandStr => randStrTextBox.Text.Trim();

        public EndfieldResourceKeySettingsForm(EndfieldResourceKeyRequest request)
        {
            Text = "Endfield resource keys";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 274);
            Font = new System.Drawing.Font("Segoe UI", 9F);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 2,
                RowCount = 7,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

            AddField(layout, 0, "App code", appCodeTextBox, request.AppCode);
            AddField(layout, 1, "Platform", platformTextBox, request.Platform);
            AddField(layout, 2, "Game version", gameVersionTextBox, request.GameVersion);
            AddField(layout, 3, "Package version", packageVersionTextBox, request.PackageVersion);
            AddField(layout, 4, "Random string", randStrTextBox, request.RandStr);

            var note = new Label
            {
                Text = "Keys refresh automatically while opening Endfield VFS files. Refresh now also checks immediately.",
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            layout.Controls.Add(note, 0, 5);
            layout.SetColumnSpan(note, 2);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
            };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var saveButton = new Button { Text = "Save", AutoSize = true };
            var refreshButton = new Button { Text = "Refresh now", AutoSize = true };
            saveButton.Click += (_, _) => SaveAndClose(refresh: false);
            refreshButton.Click += (_, _) => SaveAndClose(refresh: true);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(saveButton);
            buttons.Controls.Add(refreshButton);
            layout.Controls.Add(buttons, 0, 6);
            layout.SetColumnSpan(buttons, 2);

            Controls.Add(layout);
            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        private static void AddField(TableLayoutPanel layout, int row, string label, TextBox textBox, string value)
        {
            layout.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            }, 0, row);

            textBox.Dock = DockStyle.Fill;
            textBox.Text = value ?? string.Empty;
            layout.Controls.Add(textBox, 1, row);
        }

        private void SaveAndClose(bool refresh)
        {
            RefreshRequested = refresh;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
