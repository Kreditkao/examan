using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WindowsFormsApp2
{
    public partial class ClientForm2 : Form
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private string _currentUser;


        private bool _isAuthenticated = false;

        private Dictionary<string, StringBuilder> privateChats = new Dictionary<string, StringBuilder>();

        public ClientForm2()
        {
            InitializeComponent();
            SetAuthenticatedUi(false);
        }
        private async void btnLogin_Click(object sender, EventArgs e)
        {
            await EnsureConnectedAsync();
            _currentUser = txtUsername.Text.Trim();
            await SendLineAsync($"/login {_currentUser} {txtPassword.Text}");
        }

        private async void btnRegister_Click(object sender, EventArgs e)
        {
            await EnsureConnectedAsync();
            _currentUser = txtUsername.Text.Trim();
            await SendLineAsync($"/register {_currentUser} {txtPassword.Text}");
        }

        private async Task EnsureConnectedAsync()
        {
            if (_client != null && _client.Connected) return;

            _client = new TcpClient();
            await _client.ConnectAsync("127.0.0.1", 8888);
            _stream = _client.GetStream();

            AppendChat("Connected to server.");
            _ = ReceiveLoopAsync();

            SetAuthenticatedUi(false);
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();

            try
            {
                while (true)
                {
                    int read = await _stream.ReadAsync(buffer, 0, buffer.Length);
                    if (read == 0) break;

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    while (true)
                    {
                        var str = sb.ToString();
                        var idx = str.IndexOf('\n');
                        if (idx < 0) break;
                        var line = str.Substring(0, idx).Trim();
                        sb.Remove(0, idx + 1);

                        if (TryHandleAuthLine(line))
                            continue;

                        if (line.StartsWith("[USERS]"))
                        {
                            var users = line.Substring(8).Split(',');
                            UpdateUserList(users);
                        }
                        else if (line.StartsWith("[PM]"))
                        {
                            var msg = line.Substring(5);
                            var parts = msg.Split(new[] { ':' }, 2);
                            if (parts.Length == 2)
                            {
                                var fromUser = parts[0].Trim();
                                var message = parts[1].Trim();
                                AppendPrivateMessage(fromUser, message);
                            }
                        }
                        else if (line.StartsWith("FILE "))
                        {
                            var parts = line.Split(' ');
                            long size;
                            if (parts.Length >= 2 && long.TryParse(parts[1], out size))
                            {
                                using (var sfd = new SaveFileDialog())
                                {
                                    if (sfd.ShowDialog() == DialogResult.OK)
                                    {
                                        await ReceiveFileAsync(sfd.FileName, size);
                                        AppendChat($"Downloaded to {sfd.FileName}");
                                    }
                                }
                            }
                        }
                        else
                        {
                            AppendChat(line);
                        }
                    }
                }
            }
            catch
            {
                AppendChat("Disconnected.");
            }
            finally
            {
                // При любом разрыве соединения
                _isAuthenticated = false;
                SetAuthenticatedUi(false);
            }
        }

        // успех/ошибкa аутентификации 
        private bool TryHandleAuthLine(string line)
        {
            // Успешная авторизация
            if (line.StartsWith("OK Welcome", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("OK LOGIN", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[AUTH OK]", StringComparison.OrdinalIgnoreCase))
            {
                _isAuthenticated = true;
                SetAuthenticatedUi(true);
                AppendChat("Authenticated successfully.");
                return true;
            }

            // Ошибки авторизации
            if (line.StartsWith("ERR InvalidCredentials", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("ERR Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("ERR AlreadyOnline", StringComparison.OrdinalIgnoreCase))
            {
                _isAuthenticated = false;
                SetAuthenticatedUi(false);
                AppendChat("Authentication failed.");
                return true;
            }

            // Всё остальное 
            return false;
        }


        private void SetAuthenticatedUi(bool isAuth)
        {
            
            btnLogin.Enabled = !isAuth;
            btnRegister.Enabled = !isAuth;

            
            btnSend.Enabled = isAuth;
            btnUpload.Enabled = isAuth;
            btnDownload.Enabled = isAuth;

            
            btnExit.Enabled = (_client != null && _client.Connected);
        }

        private async Task SendLineAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\n");
            await _stream.WriteAsync(bytes, 0, bytes.Length);
        }

        private void AppendChat(string line)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendChat), line);
                return;
            }
            txtChat.AppendText(line + Environment.NewLine);
        }

        private void UpdateUserList(string[] users)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string[]>(UpdateUserList), users);
                return;
            }
            lstUsers.Items.Clear();
            foreach (var u in users)
            {
                if (!string.IsNullOrWhiteSpace(u) && !string.Equals(u, _currentUser, StringComparison.OrdinalIgnoreCase))
                {
                    lstUsers.Items.Add(u);
                }
            }
        }

        private async void btnSend_Click(object sender, EventArgs e)
        {
            if (!_isAuthenticated)
            {
                MessageBox.Show("Сначала войдите в аккаунт.");
                return;
            }

            var text = txtMessage.Text;
            if (string.IsNullOrWhiteSpace(text)) return;

            if (lstUsers.SelectedItem == null)
            {
                MessageBox.Show("Выберите пользователя из списка!");
                return;
            }

            var targetUser = lstUsers.SelectedItem.ToString();
            await SendLineAsync($"/pm {targetUser} {text}");

            if (!privateChats.ContainsKey(targetUser))
                privateChats[targetUser] = new StringBuilder();

            privateChats[targetUser].AppendLine($"Me: {text}");
            ShowChat(targetUser);

            txtMessage.Clear();
        }

        private async void btnExit_Click(object sender, EventArgs e)
        {
            try
            {
                if (_stream != null)
                {
                    await SendLineAsync("/quit");
                }
            }
            catch {  }

            try { _client?.Close(); } catch { }

            AppendChat("You left the chat.");
            lstUsers.Items.Clear();

            _isAuthenticated = false;
            SetAuthenticatedUi(false);
        }

        private async void btnUpload_Click(object sender, EventArgs e)
        {
            if (!_isAuthenticated)
            {
                MessageBox.Show("Сначала войдите в аккаунт.");
                return;
            }

            using (var ofd = new OpenFileDialog())
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    var fi = new FileInfo(ofd.FileName);

                    
                    if (lstUsers.SelectedItem == null)
                    {
                        MessageBox.Show("Выберите пользователя из списка!");
                        return;
                    }

                    var targetUser = lstUsers.SelectedItem.ToString();

                    await SendLineAsync($"/upload {targetUser} {fi.Name} {fi.Length}");

                    var buffer = new byte[8192];
                    using (var fs = fi.OpenRead())
                    {
                        int read;
                        while ((read = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await _stream.WriteAsync(buffer, 0, read);
                        }
                    }
                    AppendChat($"Uploaded {fi.Name} to {targetUser}");
                }
            }

        }

        private async void btnDownload_Click(object sender, EventArgs e)
        {
            if (!_isAuthenticated)
            {
                MessageBox.Show("Сначала войдите в аккаунт.");
                return;
            }

            var filename = txtMessage.Text.Trim();
            if (string.IsNullOrEmpty(filename))
            {
                MessageBox.Show("Введите имя файла в поле сообщения");
                return;
            }
            await SendLineAsync($"/download {filename}");
        }

        private async Task ReceiveFileAsync(string path, long size)
        {
            var buffer = new byte[8192];
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                long remaining = size;
                while (remaining > 0)
                {
                    int read = await _stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;
                    await fs.WriteAsync(buffer, 0, read);
                    remaining -= read;
                }
            }
        }

        private void AppendPrivateMessage(string fromUser, string message)
        {
            if (!privateChats.ContainsKey(fromUser))
                privateChats[fromUser] = new StringBuilder();

            privateChats[fromUser].AppendLine($"{fromUser}: {message}");

            if (lstUsers.SelectedItem != null && lstUsers.SelectedItem.ToString() == fromUser)
            {
                ShowChat(fromUser);
            }
        }

        private void ShowChat(string user)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(ShowChat), user);
                return;
            }

            txtChat.Clear();
            if (privateChats.ContainsKey(user))
                txtChat.Text = privateChats[user].ToString();
        }

        private void lstUsers_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lstUsers.SelectedItem != null)
            {
                var user = lstUsers.SelectedItem.ToString();
                ShowChat(user);
            }
        }
    }
}
