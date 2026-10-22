using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SwyfCloudTts.Setup
{
    internal sealed class SetupForm : Form
    {
        private static readonly Color Muted = Color.FromArgb(96, 96, 96);
        private static readonly Color Good = Color.FromArgb(22, 140, 64);
        private static readonly Color Warn = Color.FromArgb(214, 120, 0);
        private static readonly Color Bad = Color.FromArgb(200, 40, 40);

        private readonly Options _options;
        private readonly TextBox _gamePath = new TextBox();
        private readonly Button _browse = new Button();
        private readonly Label _statusDot = new Label();
        private readonly Label _statusText = new Label();
        private readonly Label _cardText = new Label();
        private readonly Button _install = new Button();
        private readonly Button _uninstall = new Button();
        private readonly CheckBox _removeSettings = new CheckBox();
        private readonly TextBox _log = new TextBox();
        private readonly LinkLabel _openFolder = new LinkLabel();

        private InstallStatus _status = new InstallStatus { State = InstallState.GameNotFound };
        private bool _busy = true;

        public SetupForm(Options options)
        {
            _options = options;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9.75F);
            Text = "SWYF Cloud TTS " + Installer.Version;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 500);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location);
            }
            catch (Exception)
            {
                // The default window icon is fine.
            }

            BuildLayout();
            ResumeLayout(false);
            PerformLayout();

            Shown += async (sender, args) => await StartAsync();
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(22, 18, 22, 14),
                ColumnCount = 1,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            var title = new Label
            {
                Text = "SWYF Cloud TTS",
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 17F),
                Margin = new Padding(0, 0, 0, 0),
            };
            var subtitle = new Label
            {
                Text = Strings.Subtitle,
                AutoSize = true,
                ForeColor = Muted,
                Margin = new Padding(2, 0, 0, 16),
            };

            var folderLabel = new Label
            {
                Text = Strings.GameFolder,
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(2, 0, 0, 4),
            };

            var folderRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _gamePath.ReadOnly = true;
            _gamePath.Dock = DockStyle.Fill;
            _gamePath.Margin = new Padding(2, 4, 8, 0);
            _browse.Text = Strings.Browse;
            _browse.AutoSize = true;
            _browse.Padding = new Padding(8, 2, 8, 2);
            _browse.Click += async (sender, args) => await BrowseAsync();
            folderRow.Controls.Add(_gamePath, 0, 0);
            folderRow.Controls.Add(_browse, 1, 0);

            var statusRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
            _statusDot.Text = "●";
            _statusDot.AutoSize = true;
            _statusDot.Font = new Font("Segoe UI", 12F);
            _statusDot.Margin = new Padding(0, 0, 2, 0);
            _statusText.AutoSize = true;
            _statusText.Font = new Font(Font, FontStyle.Bold);
            _statusText.Margin = new Padding(0, 5, 0, 0);
            statusRow.Controls.Add(_statusDot);
            statusRow.Controls.Add(_statusText);

            _cardText.AutoSize = true;
            _cardText.ForeColor = Muted;
            _cardText.MaximumSize = new Size(570, 0);
            _cardText.Margin = new Padding(24, 0, 0, 14);

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            _install.Size = new Size(180, 36);
            _install.Font = new Font(Font, FontStyle.Bold);
            _install.Margin = new Padding(0, 0, 10, 0);
            _install.Click += async (sender, args) => await RunAsync(install: true, confirmed: false);
            _uninstall.Text = Strings.Uninstall;
            _uninstall.Size = new Size(140, 36);
            _uninstall.Margin = new Padding(0);
            _uninstall.Click += async (sender, args) => await RunAsync(install: false, confirmed: false);
            buttons.Controls.Add(_install);
            buttons.Controls.Add(_uninstall);

            _removeSettings.Text = Strings.RemoveSettings;
            _removeSettings.AutoSize = true;
            _removeSettings.ForeColor = Muted;
            _removeSettings.Checked = _options.Purge;
            _removeSettings.Margin = new Padding(2, 0, 0, 10);

            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill;
            _log.BackColor = SystemColors.Window;
            _log.Text = Strings.WhatItDoes;
            _log.Margin = new Padding(2, 0, 2, 8);

            _openFolder.Text = Strings.OpenSettingsFolder;
            _openFolder.AutoSize = true;
            _openFolder.Margin = new Padding(2, 0, 0, 0);
            _openFolder.LinkClicked += (sender, args) => OpenSettingsFolder();

            Control[] rows = { title, subtitle, folderLabel, folderRow, statusRow, _cardText, buttons, _removeSettings, _log, _openFolder };
            root.RowCount = rows.Length;
            foreach (Control row in rows)
            {
                root.RowStyles.Add(row == _log ? new RowStyle(SizeType.Percent, 100F) : new RowStyle(SizeType.AutoSize));
                root.Controls.Add(row);
            }

            AcceptButton = _install;
            UpdateControls();
        }

        private async Task StartAsync()
        {
            string game = _options.Game != null ? GameLocator.Normalize(_options.Game) : GameLocator.Find();
            _gamePath.Text = game ?? "";
            await RefreshStatusAsync();

            if (_options.Run)
            {
                await RunAsync(install: !_options.Uninstall, confirmed: true);
            }
            else if (_options.Uninstall)
            {
                AppendLog(Strings.UninstallPrompt);
                AcceptButton = _uninstall;
                _uninstall.Focus();
            }
        }

        private async Task RefreshStatusAsync()
        {
            _busy = true;
            UpdateControls();
            string game = _gamePath.Text;
            try
            {
                _status = await Task.Run(() => Installer.Probe(game));
            }
            catch (Exception ex)
            {
                SetupLog.Write("status check failed: " + ex);
                _status = new InstallStatus { State = InstallState.GameNotFound };
            }

            _busy = false;
            UpdateControls();
        }

        private void UpdateControls()
        {
            bool installed = _status.State == InstallState.Installed || _status.State == InstallState.Outdated ||
                             _status.State == InstallState.NeedsRepair;

            switch (_status.State)
            {
                case InstallState.GameNotFound:
                    SetStatus(Bad, Strings.StatusGameNotFound);
                    _install.Text = Strings.Install;
                    break;
                case InstallState.NotInstalled:
                    SetStatus(Muted, Strings.StatusNotInstalled);
                    _install.Text = Strings.Install;
                    break;
                case InstallState.Installed:
                    SetStatus(Good, string.Format(Strings.StatusInstalled, _status.InstalledVersion));
                    _install.Text = Strings.Reinstall;
                    break;
                case InstallState.Outdated:
                    SetStatus(Warn, string.Format(Strings.StatusOutdated, _status.InstalledVersion, Installer.Version));
                    _install.Text = Strings.Update;
                    break;
                case InstallState.NeedsRepair:
                    SetStatus(Warn, Strings.StatusNeedsRepair);
                    _install.Text = Strings.Repair;
                    break;
            }

            _cardText.Text = !installed ? ""
                : _status.CardInstalled ? Strings.CardInstalled
                : _status.CardBlocked ? Strings.CardBlocked
                : Strings.CardMissing;
            _cardText.Visible = _cardText.Text.Length > 0;

            bool gameFound = _status.State != InstallState.GameNotFound;
            _install.Enabled = !_busy && gameFound;
            _uninstall.Enabled = !_busy && (installed || (!gameFound && Installer.IsRegistered()));
            _browse.Enabled = !_busy;
            _removeSettings.Enabled = !_busy;
            UseWaitCursor = _busy;
        }

        private void SetStatus(Color color, string text)
        {
            _statusDot.ForeColor = color;
            _statusText.Text = text;
        }

        private async Task BrowseAsync()
        {
            using (var dialog = new FolderBrowserDialog { Description = Strings.BrowseHint, ShowNewFolderButton = false })
            {
                if (Directory.Exists(_gamePath.Text))
                {
                    dialog.SelectedPath = _gamePath.Text;
                }

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                string game = GameLocator.Normalize(dialog.SelectedPath);
                if (game == null)
                {
                    MessageBox.Show(this, Strings.NotAGameFolder, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _gamePath.Text = game;
                await RefreshStatusAsync();
            }
        }

        private async Task RunAsync(bool install, bool confirmed)
        {
            if (_busy)
            {
                return;
            }

            string game = _gamePath.Text;
            bool purge = _removeSettings.Checked;

            if (!install && !confirmed &&
                MessageBox.Show(this, Strings.ConfirmUninstall, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            if (GameLocator.IsGameDir(game) && !Installer.CanWrite(game) && !Elevation.IsElevated)
            {
                if (MessageBox.Show(this, Strings.NeedAdmin, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes &&
                    Elevation.Relaunch(_options.ElevatedArguments(install, game, purge)))
                {
                    Close();
                    return;
                }

                AppendLog(Strings.AdminCancelled);
                return;
            }

            _busy = true;
            UpdateControls();
            _log.Clear();

            bool succeeded = false;
            try
            {
                var installer = new Installer(game, _options.StateDirectory, _options.Integrate,
                    line => BeginInvoke(new Action(() => AppendLog(line))));
                await Task.Run(() =>
                {
                    if (install)
                    {
                        installer.Install();
                    }
                    else
                    {
                        installer.Uninstall(purge);
                    }
                });

                succeeded = true;
            }
            catch (SetupException ex)
            {
                SetupLog.Write(ex.ToString());
                AppendLog(ex.Message);
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                SetupLog.Write(ex.ToString());
                string message = string.Format(Strings.Failed, ex.Message);
                AppendLog(message);
                AppendLog(string.Format(Strings.LogHint, SetupLog.Path));
                MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            await RefreshStatusAsync();

            if (succeeded)
            {
                string done = install ? Strings.DoneInstall : Strings.DoneUninstall;
                AppendLog(done);
                MessageBox.Show(this, done, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

                // Uninstalling from "Apps & features" runs the copy in the profile, which is cleaned up on exit.
                if (!install && Installer.DeleteOnExit.Count > 0)
                {
                    Close();
                }
            }
        }

        private void AppendLog(string line)
        {
            if (_log.TextLength > 0)
            {
                _log.AppendText(Environment.NewLine);
            }

            _log.AppendText(line);
        }

        private void OpenSettingsFolder()
        {
            try
            {
                Directory.CreateDirectory(_options.StateDirectory);
                Process.Start("explorer.exe", "\"" + _options.StateDirectory + "\"");
            }
            catch (Exception ex)
            {
                AppendLog(ex.Message);
            }
        }
    }
}
