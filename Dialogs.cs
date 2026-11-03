namespace BudsTray;

/// A small question dialog with its own button captions. MessageBox takes its button
/// captions from the Windows display language, and "Pair" / "Next device" say more than "Yes" / "No".
static class Dialogs
{
    /// Shows the dialog and returns the index of the clicked button, or -1 if it was closed.
    /// The last button is the Cancel button (Esc); <paramref name="defaultButton"/> gets Enter.
    public static int Ask(string message, string[] buttons, int defaultButton = 0, Icon? icon = null)
    {
        using var form = new Form
        {
            Text = "BudsTray",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = true,
            TopMost = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = SystemFonts.MessageBoxFont,
            Padding = new Padding(0),
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Padding = new Padding(16, 16, 16, 8) };
        layout.Controls.Add(new PictureBox
        {
            Image = (icon ?? SystemIcons.Question).ToBitmap(),
            SizeMode = PictureBoxSizeMode.AutoSize,
            Margin = new Padding(0, 0, 12, 0),
        }, 0, 0);
        layout.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 2, 0, 12) }, 1, 0);

        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0) };
        int result = -1;
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            int index = i;
            var button = new Button { Text = buttons[i], AutoSize = true, MinimumSize = new Size(88, 0) };
            button.Click += (_, _) => { result = index; form.Close(); };
            row.Controls.Add(button);
            if (i == defaultButton) form.AcceptButton = button;
            if (i == buttons.Length - 1) form.CancelButton = button;
        }
        layout.Controls.Add(row, 0, 1);
        layout.SetColumnSpan(row, 2);
        form.Controls.Add(layout);
        form.Shown += (_, _) => { form.Activate(); (form.AcceptButton as Button)?.Focus(); };
        form.ShowDialog();
        return result;
    }
}
