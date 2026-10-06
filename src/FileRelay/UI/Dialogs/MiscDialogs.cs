using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>Windows 방화벽 규칙 확인/추가.</summary>
    public static class Firewall
    {
        public enum State { Unknown, Allowed, Blocked, NoRule }

        /// <summary>
        /// 이 실행 파일(또는 이 포트)의 인바운드 규칙 상태 (COM, 백그라운드에서 호출 권장).
        /// 프로그램 규칙뿐 아니라 포트 규칙(TCP, 해당 포트)도 허용으로 친다. 프로그램 차단 규칙이 있으면 차단.
        /// </summary>
        public static State Check(int port = 0)
        {
            try
            {
                var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null) return State.Unknown;
                dynamic pol = Activator.CreateInstance(t);
                bool allow = false, block = false;
                string portText = port > 0 ? port.ToString() : null;
                foreach (dynamic r in pol.Rules)
                {
                    try
                    {
                        if (!(bool)r.Enabled || (int)r.Direction != 1) continue;
                        string app = r.ApplicationName;
                        if (!string.IsNullOrEmpty(app))
                        {
                            if (!PathUtil.SameLocalPath(app, AppPaths.ExePath)) continue;
                            if ((int)r.Action == 1) allow = true; else block = true;
                        }
                        else if (portText != null && (int)r.Action == 1 && (int)r.Protocol == 6)
                        {
                            string ports = r.LocalPorts ?? "";
                            foreach (var p in ports.Split(','))
                                if (p.Trim() == portText) allow = true;
                        }
                    }
                    catch { }
                }
                // 프로필이 꺼져 있으면 규칙과 무관하게 허용.
                try
                {
                    int profiles = pol.CurrentProfileTypes;
                    bool anyOn = false;
                    foreach (int p in new[] { 1, 2, 4 })
                        if ((profiles & p) != 0 && (bool)pol.FirewallEnabled[p]) anyOn = true;
                    if (!anyOn) return State.Allowed;
                }
                catch { }
                if (block) return State.Blocked;
                return allow ? State.Allowed : State.NoRule;
            }
            catch { return State.Unknown; }
        }

        /// <summary>
        /// 관리자 권한으로 허용 규칙 추가:
        ///  · 이 exe 의 기존 규칙(처음 실행 때 '취소'로 생긴 차단 규칙 포함)을 지우고 허용 규칙 추가
        ///  · exe 를 다른 폴더로 옮겨도 되도록 포트 규칙(TCP/UDP)도 추가
        ///  · 모든 네트워크 프로필(도메인/개인/공용) — 회사망이 '공용'으로 잡혀도 동작
        /// </summary>
        public static void AddRuleInteractive(IWin32Window owner)
        {
            string exe = AppPaths.ExePath;
            int port = AppCore.I.Config.Port;
            string tcp = $"FileRelay TCP {port}", udp = $"FileRelay UDP {port}";
            string args = "/c " +
                $"netsh advfirewall firewall delete rule name=all program=\"{exe}\" & " +
                $"netsh advfirewall firewall delete rule name=\"{tcp}\" & " +
                $"netsh advfirewall firewall delete rule name=\"{udp}\" & " +
                $"netsh advfirewall firewall add rule name=\"FileRelay\" dir=in action=allow program=\"{exe}\" enable=yes profile=any & " +
                $"netsh advfirewall firewall add rule name=\"{tcp}\" dir=in action=allow protocol=TCP localport={port} enable=yes profile=any & " +
                $"netsh advfirewall firewall add rule name=\"{udp}\" dir=in action=allow protocol=UDP localport={port} enable=yes profile=any";
            try
            {
                var p = Process.Start(new ProcessStartInfo("cmd.exe", args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                p?.WaitForExit(15000);
                var st = Check(port);
                if (st == State.Allowed || st == State.Unknown)
                {
                    Log.Success(Cat.Sys, T("fw.log.added"));
                    MessageDialog.Show(owner, T("fw.addedTitle"), T("fw.addedText"), MsgKind.Success);
                }
                else MessageDialog.Warn(owner, T("fw.checkTitle"), T("fw.stillBlocked"));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // 사용자가 UAC 를 취소함.
            }
            catch (Exception ex)
            {
                MessageDialog.Error(owner, T("fw.failedTitle"), ex.Message);
            }
        }
    }

    /// <summary>처음 실행 시 이름/포트/첫 공유 폴더를 받는다.</summary>
    public sealed class FirstRunDialog : DialogBase
    {
        readonly DarkTextBox _name, _port, _share;
        readonly DarkSelect _access, _lang;
        readonly DarkCheckBox _autostart;
        public AppConfig Result;

        /// <summary>언어를 바꿔 다시 열 때 이어 갈 입력 (설정에 없는 것들).</summary>
        public sealed class Draft { public string Share = ""; public int Access; public bool AutoStart = true; }
        /// <summary>DialogResult.Retry — 언어가 바뀌었다. Result(언어 포함)와 Next 로 다시 연다.</summary>
        public Draft Next;

        public FirstRunDialog(AppConfig cfg, Draft draft = null)
        {
            draft = draft ?? new Draft();
            Text = T("first.title");
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            int W = Theme.S(560), pad = Theme.S(28), y = Theme.S(24);
            Body.Controls.Add(new PictureBox { Image = Icons.AppBitmap(Theme.S(52)), Size = new Size(Theme.S(52), Theme.S(52)), Location = new Point(pad, y) });
            int tl = pad + Theme.S(68);
            Body.Controls.Add(new Label { Text = T("first.welcome"), Font = Theme.Big, ForeColor = Theme.Text, AutoSize = true, Location = new Point(tl, y) });
            var sub = new NoteLabel(T("first.intro")) { Location = new Point(tl, y + Theme.S(34)) };
            sub.FitHeight(W - tl - pad);
            Body.Controls.Add(sub);
            y = Math.Max(sub.Bottom, y + Theme.S(52)) + Theme.S(22);

            int lw = Theme.S(120), fw = W - pad * 2 - lw;
            Label L(string t, int yy) { var l = new Label { Text = t, ForeColor = Theme.TextDim, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(pad, yy), Size = new Size(lw, Theme.S(34)) }; Body.Controls.Add(l); return l; }

            L(T("set.gen.name"), y);
            _name = new DarkTextBox { Location = new Point(pad + lw, y), Size = new Size(Theme.S(240), Theme.S(34)), Text = string.IsNullOrWhiteSpace(cfg.UserName) ? Environment.UserName : cfg.UserName, Placeholder = T("set.gen.namePh") };
            Body.Controls.Add(_name);
            y += Theme.S(44);

            L(T("set.gen.port"), y);
            _port = new DarkTextBox { Location = new Point(pad + lw, y), Size = new Size(Theme.S(110), Theme.S(34)), Text = cfg.Port.ToString() };
            _port.Box.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            Body.Controls.Add(_port);
            Body.Controls.Add(new Label { Text = T("first.portHint"), ForeColor = Theme.TextFaint, Font = Theme.Small, AutoSize = true, Location = new Point(pad + lw + Theme.S(122), y + Theme.S(9)) });
            y += Theme.S(52);

            Body.Controls.Add(new Label { Text = T("first.shareSection"), Font = Theme.UiBold, ForeColor = Theme.Text, AutoSize = true, Location = new Point(pad, y) });
            y += Theme.S(28);
            L(T("common.folder"), y);
            _share = new DarkTextBox { Location = new Point(pad + lw, y), Size = new Size(fw - Theme.S(44), Theme.S(34)), Placeholder = T("first.sharePh"), Text = draft.Share };
            var browse = new FlatButton("", ButtonKind.Secondary, G.OpenFolder) { Location = new Point(pad + lw + fw - Theme.S(38), y), Size = new Size(Theme.S(38), Theme.S(34)) };
            browse.Click += (s, e) => { var p = FolderPicker.Pick(this, T("set.share.pick")); if (p != null) _share.Text = p; };
            Body.Controls.Add(_share);
            Body.Controls.Add(browse);
            y += Theme.S(44);
            L(T("set.share.colAccess"), y);
            _access = new DarkSelect { Location = new Point(pad + lw, y), Size = new Size(fw, Theme.S(34)) };
            foreach (var a in new[] { AccessLevel.Read, AccessLevel.Write, AccessLevel.Full }) _access.Add(a, Fmt.AccessLong(a));
            _access.SelectedIndex = Math.Max(0, Math.Min(2, draft.Access));
            Body.Controls.Add(_access);
            y += Theme.S(52);

            _autostart = new DarkCheckBox(T("first.autostart")) { Location = new Point(pad, y), Checked = draft.AutoStart };
            Body.Controls.Add(_autostart);
            y += Theme.S(40);
            ClientSize = new Size(W, y + Footer.Height);

            Result = cfg;
            // 언어 — 고르면 그 언어로 창을 다시 연다 (입력한 내용은 유지).
            _lang = new DarkSelect { Size = new Size(Theme.S(220), Theme.S(32)), Margin = Padding.Empty };
            _lang.Add("auto", T("set.adv.langAuto"));
            _lang.Add("ko", "한국어");
            _lang.Add("en", "English");
            _lang.SelectedValue = cfg.Language;
            if (_lang.SelectedIndex < 0) _lang.SelectedIndex = 0;
            _lang.SelectedIndexChanged += (s, e) =>
            {
                string lang = (string)_lang.SelectedValue ?? "auto";
                var c = Result.Clone();
                c.Language = lang;
                if (_name.Text.Trim().Length > 0) c.UserName = _name.Text.Trim();
                if (int.TryParse(_port.Text, out int p)) c.Port = p;
                Result = c;
                if (FileRelay.L.Resolve(lang) == FileRelay.L.Lang) return;   // 화면 언어가 그대로면 다시 열 필요 없음 (L 은 이 생성자의 지역 함수 이름과 겹친다)
                Next = new Draft { Share = _share.Text, Access = _access.SelectedIndex, AutoStart = _autostart.Checked };
                DialogResult = DialogResult.Retry;
            };
            new ToolTip().SetToolTip(_lang, T("set.adv.language"));
            FooterLeft.Controls.Add(_lang);
            AddButton(T("common.exit"), DialogResult.Cancel);
            var ok = AddButton(T("first.start"), DialogResult.None, ButtonKind.Primary, G.Play);
            ok.Click += (s, e) => Accept();
            AcceptButton = ok;
        }

        void Accept()
        {
            string name = _name.Text.Trim();
            if (name.Length == 0) { MessageDialog.Warn(this, T("common.name"), T("first.nameEmpty")); _name.Focus(); return; }
            if (!int.TryParse(_port.Text, out int port) || port < 1024 || port > 65535) { MessageDialog.Warn(this, T("set.gen.port"), T("cfgerr.port")); _port.Focus(); return; }
            string share = _share.Text.Trim();
            if (share.Length > 0 && !Directory.Exists(share)) { MessageDialog.Warn(this, T("common.folder"), T("set.share.noFolder", share)); _share.Focus(); return; }
            var c = Result.Clone();
            c.UserName = name;
            c.Port = port;
            c.StartWithWindows = _autostart.Checked;
            c.Language = (string)_lang.SelectedValue ?? "auto";
            if (share.Length > 0)
            {
                string sn = ShareConfig.DefaultNameFor(share);
                c.Shares.Add(new ShareConfig { Name = sn, Path = share, Access = (AccessLevel)_access.SelectedValue, Enabled = true });
            }
            Result = c;
            DialogResult = DialogResult.OK;
        }
    }

    /// <summary>프로그램 정보 + 내 주소.</summary>
    public sealed class AboutDialog : DialogBase
    {
        public AboutDialog()
        {
            Text = T("about.title");
            var c = AppCore.I;
            int W = Theme.S(480), pad = Theme.S(26), y = Theme.S(24);
            Body.Controls.Add(new PictureBox { Image = Icons.AppBitmap(Theme.S(56)), Size = new Size(Theme.S(56), Theme.S(56)), Location = new Point(pad, y) });
            int tl = pad + Theme.S(72);
            Body.Controls.Add(new Label { Text = "FileRelay", Font = Theme.Big, ForeColor = Theme.Text, AutoSize = true, Location = new Point(tl, y) });
            Body.Controls.Add(new Label { Text = T("about.version", AppInfo.Version), ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(tl, y + Theme.S(32)) });
            y += Theme.S(76);
            string info =
                $"{T("set.gen.name")}\t{c.Config.UserName}\r\n" +
                $"{T("about.address")}\t{string.Join(", ", NetUtil.LocalIPv4())}  {T("about.port", c.Config.Port)}\r\n" +
                $"{T("about.pcName")}\t{Environment.MachineName}\r\n" +
                $"{T("about.instanceId")}\t{c.MyId}\r\n" +
                $"{T("set.adv.config")}\t{AppPaths.ConfigFile}\r\n" +
                $"{T("set.adv.data")}\t{AppPaths.DataDir}\r\n" +
                $".NET\t{Environment.Version}";
            var tb = new DarkTextBox { Multiline = true, ReadOnly = true, Location = new Point(pad, y), Size = new Size(W - pad * 2, Theme.S(170)) };
            tb.Box.Font = Theme.Small;
            tb.Text = info;
            // 탭 위치를 가장 긴 이름에 맞춘다 (기본 탭 간격이면 언어에 따라 줄이 어긋난다). 단위: 대화 상자 단위 (평균 글자 폭/4)
            int labelPx = info.Split('\n').Max(l => TextRenderer.MeasureText(l.Split('\t')[0], Theme.Small).Width) + Theme.S(14);
            int avg = Math.Max(1, TextRenderer.MeasureText("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ", Theme.Small).Width / 52);
            void Tabs() { Native.SendMessage(tb.Box.Handle, 0x00CB /* EM_SETTABSTOPS */, (IntPtr)1, new[] { labelPx * 4 / avg }); tb.Box.Invalidate(); }
            if (tb.Box.IsHandleCreated) Tabs(); else tb.Box.HandleCreated += (s, e) => Tabs();
            Body.Controls.Add(tb);
            y = tb.Bottom + Theme.S(14);
            ClientSize = new Size(W, y + Footer.Height);
            var copy = new FlatButton(T("about.copy"), ButtonKind.Ghost, G.Copy);
            copy.AutoFit();
            copy.Click += (s, e) => { try { Clipboard.SetText(info); } catch { } };
            FooterLeft.Controls.Add(copy);
            AddButton(T("common.close"), DialogResult.OK, ButtonKind.Primary);
        }
    }

    /// <summary>단축키 도움말.</summary>
    public sealed class ShortcutsDialog : DialogBase
    {
        static readonly string[,] Rows =
        {
            { T("keys.sec.files"), "" },
            { T("keys.k.enter"), T("keys.open") },
            { "Backspace · Alt+↑", T("keys.up") },
            { T("keys.k.back"), T("keys.back") },
            { T("keys.k.forward"), T("keys.forward") },
            { "F5 · Ctrl+R", T("common.refresh") },
            { "Ctrl+L · Alt+D · F4", T("keys.address") },
            { "Ctrl+F", T("keys.filter") },
            { "Ctrl+A", T("keys.selectAll") },
            { "Ctrl+C / Ctrl+V", T("keys.copyPaste") },
            { "Ctrl+T · Ctrl+Enter", T("keys.transfer") },
            { "F2", T("common.rename") },
            { "Delete", T("keys.delete") },
            { "Ctrl+Shift+N", T("common.newFolder") },
            { "Alt+Enter", T("keys.props") },
            { "Tab", T("keys.switchPane") },
            { T("keys.k.drag"), T("keys.drag") },
            { T("keys.sec.global"), "" },
            { "Ctrl+1 / 2 / 3", T("keys.focus") },
            { "Ctrl+N", T("keys.addPeer") },
            { "Ctrl+M", T("keys.sendNote") },
            { "Ctrl+Shift+M", T("keys.notes") },
            { "Ctrl+P", T("keys.pause") },
            { "Ctrl+,", T("common.settings") },
            { "Ctrl+Q", T("common.exit") },
            { T("keys.sec.note"), "" },
            { "Ctrl+Enter", T("common.send") },
            { "Ctrl+R", T("keys.reply") },
            { "Esc", T("common.close") },
        };

        public ShortcutsDialog()
        {
            Text = T("keys.title");
            int W = Theme.S(640), pad = Theme.S(24), y = Theme.S(18);
            Body.Controls.Add(new Label { Text = T("keys.title"), Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Location = new Point(pad, y) });
            y += Theme.S(40);
            var panel = new BufferedPanel { Location = new Point(pad, y), Size = new Size(W - pad * 2, 10) };
            int rows = Rows.GetLength(0);
            int rh = Theme.S(25);
            panel.Height = rows * rh;
            panel.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.Clear(Theme.Bg);
                Theme.HQ(g);
                for (int i = 0; i < rows; i++)
                {
                    string k = Rows[i, 0], d = Rows[i, 1];
                    var r = new Rectangle(0, i * rh, panel.Width, rh);
                    if (d.Length == 0)
                    {
                        Theme.DrawText(g, k, Theme.SmallBold, Theme.Accent, new Rectangle(r.X, r.Y + Theme.S(4), r.Width, r.Height));
                        continue;
                    }
                    var ks = Theme.Measure(k, Theme.Small);
                    var kr = new Rectangle(r.X, r.Y + Theme.S(3), ks.Width + Theme.S(14), rh - Theme.S(6));
                    Theme.FillRound(g, Theme.Raised, kr, Theme.S(4));
                    Theme.DrawText(g, k, Theme.Small, Theme.Text, kr, Theme.TextCenter);
                    Theme.DrawText(g, d, Theme.Ui, Theme.TextDim, new Rectangle(Theme.S(190), r.Y, r.Width - Theme.S(190), r.Height));
                }
            };
            Body.Controls.Add(panel);
            y = panel.Bottom + Theme.S(14);
            ClientSize = new Size(W, y + Footer.Height);
            AddButton(T("common.close"), DialogResult.OK, ButtonKind.Primary);
        }
    }
}
