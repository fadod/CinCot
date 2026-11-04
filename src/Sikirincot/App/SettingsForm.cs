using Sikirincot.Core;

namespace Sikirincot.App;

internal sealed class SettingsForm : Form
{
    public SettingsForm(AppSettings settings)
    {
        Text = "Sikirincot settings";
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(520, 520);
        MinimumSize = new Size(420, 400);

        var draft = settings.Clone();
        var grid = new PropertyGrid
        {
            Dock = DockStyle.Fill,
            SelectedObject = draft,
            ToolbarVisible = false,
            PropertySort = PropertySort.Categorized,
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        save.Click += (_, _) =>
        {
            settings.CopyFrom(draft);
            settings.Save();
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        Controls.Add(grid);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
