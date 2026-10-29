using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Tap.Plugins.Workflow;

namespace OpenTap.WpfEditor;

public partial class InstrumentDebugWindow : Window
{
    private SerialCommandInstrument? activeSerial;
    private ScpiVisaInstrument? activeVisa;
    private TcpClient? tcpClient;
    private NetworkStream? tcpStream;
    private bool isBusy;

    public InstrumentDebugWindow()
    {
        InitializeComponent();
        TransportComboBox.ItemsSource = new[] { "Serial", "VISA / USB", "TCP/IP" };
        TransportComboBox.SelectedIndex = 0;
        TerminatorComboBox.ItemsSource = new[] { "CRLF", "LF", "CR", "None" };
        TerminatorComboBox.SelectedIndex = 0;
        EncodingComboBox.ItemsSource = new[] { "ASCII", "UTF-8", "Unicode" };
        EncodingComboBox.SelectedIndex = 0;
        UpdateTransportControls();
    }

    private bool IsConnected => activeSerial != null || activeVisa != null || tcpClient?.Connected == true;

    private void TransportComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TransportComboBox == null) return;
        UpdateTransportControls();
    }

    private void UpdateTransportControls()
    {
        var transport = TransportComboBox.SelectedItem as string ?? "Serial";
        var isTcp = transport == "TCP/IP";
        var isVisa = transport == "VISA / USB";
        ResourcePanel.Visibility = isTcp ? Visibility.Collapsed : Visibility.Visible;
        TcpPanel.Visibility = isTcp ? Visibility.Visible : Visibility.Collapsed;
        ResourceLabel.Text = isVisa ? "VISA resource" : "Serial instrument";
        ResourceComboBox.ItemsSource = isVisa
            ? InstrumentSettings.Current.OfType<ScpiVisaInstrument>().Cast<object>().ToList()
            : InstrumentSettings.Current.OfType<SerialCommandInstrument>().Cast<object>().ToList();
        TerminatorComboBox.IsEnabled = !isVisa && !IsConnected;
        EncodingComboBox.IsEnabled = isTcp && !IsConnected;
        TransportComboBox.IsEnabled = !IsConnected && !isBusy;
        ResourceComboBox.IsEnabled = !IsConnected && !isBusy;
        HostTextBox.IsEnabled = !IsConnected && !isBusy;
        PortTextBox.IsEnabled = !IsConnected && !isBusy;
        ConnectButton.IsEnabled = !IsConnected && !isBusy;
        DisconnectButton.IsEnabled = IsConnected && !isBusy;
        SendButton.IsEnabled = IsConnected && !isBusy;
        ReadResponseCheckBox.IsEnabled = !isBusy;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TimeoutTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutMs) || timeoutMs <= 0)
        {
            SetStatus("Timeout must be a positive number.");
            return;
        }

        SetBusy(true);
        try
        {
            var transport = TransportComboBox.SelectedItem as string;
            if (transport == "Serial")
            {
                if (ResourceComboBox.SelectedItem is not SerialCommandInstrument serial)
                    throw new InvalidOperationException("Select a configured serial instrument.");
                if (serial.IsConnected) throw new InvalidOperationException("This serial instrument is already open.");
                await Task.Run(serial.Open);
                activeSerial = serial;
            }
            else if (transport == "VISA / USB")
            {
                if (ResourceComboBox.SelectedItem is not ScpiVisaInstrument visa)
                    throw new InvalidOperationException("Select a configured VISA instrument.");
                if (visa.IsConnected) throw new InvalidOperationException("This VISA instrument is already open.");
                await Task.Run(visa.Open);
                activeVisa = visa;
            }
            else
            {
                if (!int.TryParse(PortTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                    throw new InvalidOperationException("Port must be between 1 and 65535.");
                var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(HostTextBox.Text.Trim(), port).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
                    tcpClient = client;
                    tcpStream = client.GetStream();
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }

            ConnectionStatusText.Text = "Connected";
            SetStatus("Connected.");
            AppendOutput("Connected using " + transport + ".");
        }
        catch (Exception exception)
        {
            ConnectionStatusText.Text = "Disconnected";
            SetStatus(exception.Message);
            AppendOutput("CONNECT ERROR: " + exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await Task.Run(CloseActiveConnection);
            SetStatus("Disconnected.");
            AppendOutput("Disconnected.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
            AppendOutput("DISCONNECT ERROR: " + exception.Message);
        }
        finally
        {
            ConnectionStatusText.Text = "Disconnected";
            SetBusy(false);
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var command = CommandTextBox.Text;
        if (string.IsNullOrWhiteSpace(command))
        {
            SetStatus("Enter a command.");
            return;
        }
        if (!int.TryParse(TimeoutTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutMs) || timeoutMs <= 0)
        {
            SetStatus("Timeout must be a positive number.");
            return;
        }

        var waitForResponse = ReadResponseCheckBox.IsChecked == true;
        var transport = TransportComboBox.SelectedItem as string ?? "Serial";
        var commandToSend = transport == "VISA / USB" ? command : AddTerminator(command);
        var encoding = GetSelectedEncoding();
        SetBusy(true);
        try
        {
            var response = await Task.Run(() => ExecuteCommand(transport, commandToSend, encoding, timeoutMs, waitForResponse));
            var display = $"[{DateTime.Now:HH:mm:ss.fff}] TX: {command}{Environment.NewLine}";
            if (waitForResponse)
                display += $"[{DateTime.Now:HH:mm:ss.fff}] RX: {response}";
            else
                display += $"[{DateTime.Now:HH:mm:ss.fff}] Sent without reading a response.";
            AppendOutput(display);
            SetStatus(waitForResponse ? "Command completed." : "Command sent.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
            AppendOutput($"[{DateTime.Now:HH:mm:ss.fff}] COMMAND ERROR: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string ExecuteCommand(string transport, string command, Encoding encoding, int timeoutMs, bool waitForResponse)
    {
        if (transport == "Serial")
        {
            var serial = activeSerial ?? throw new InvalidOperationException("Serial instrument is not connected.");
            var serialEncoding = Encoding.GetEncoding(serial.EncodingName);
            var bytes = serialEncoding.GetBytes(command);
            if (!waitForResponse)
            {
                serial.Send(bytes);
                return string.Empty;
            }
            return serialEncoding.GetString(serial.Exchange(bytes, timeoutMs));
        }

        if (transport == "VISA / USB")
        {
            var visa = activeVisa ?? throw new InvalidOperationException("VISA instrument is not connected.");
            return waitForResponse ? visa.Query(command, timeoutMs) : SendVisaCommand(visa, command);
        }

        var stream = tcpStream ?? throw new InvalidOperationException("TCP instrument is not connected.");
    var payload = encoding.GetBytes(command);
        stream.Write(payload, 0, payload.Length);
    return waitForResponse ? ReadTcpResponse(stream, encoding, timeoutMs) : string.Empty;
    }

    private static string SendVisaCommand(ScpiVisaInstrument visa, string command)
    {
        visa.Write(command);
        return string.Empty;
    }

    private string AddTerminator(string command)
    {
        var terminator = (TerminatorComboBox.SelectedItem as string) switch
        {
            "CRLF" => "\r\n",
            "LF" => "\n",
            "CR" => "\r",
            _ => string.Empty
        };
        if (terminator.Length == 0 || command.EndsWith("\r", StringComparison.Ordinal) ||
            command.EndsWith("\n", StringComparison.Ordinal)) return command;
        return command + terminator;
    }

    private Encoding GetSelectedEncoding() => (EncodingComboBox.SelectedItem as string) switch
    {
        "UTF-8" => Encoding.UTF8,
        "Unicode" => Encoding.Unicode,
        _ => Encoding.ASCII
    };

    private static string ReadTcpResponse(NetworkStream stream, Encoding encoding, int timeoutMs)
    {
        using var response = new MemoryStream();
        var buffer = new byte[2048];
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            var remainingMs = Math.Max(1, timeoutMs - (int)stopwatch.ElapsedMilliseconds);
            stream.ReadTimeout = response.Length == 0 ? remainingMs : Math.Min(remainingMs, 100);
            try
            {
                var count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                response.Write(buffer, 0, count);
            }
            catch (IOException) when (response.Length > 0)
            {
                break;
            }
            catch (IOException)
            {
                return "<no response before timeout>";
            }
        }
        return response.Length == 0 ? "<empty response>" : encoding.GetString(response.ToArray());
    }

    private void CommandTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && SendButton.IsEnabled)
        {
            e.Handled = true;
            SendButton_Click(sender, e);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => ResponseTextBox.Clear();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        isBusy = busy;
        ConnectButton.Content = busy && !IsConnected ? "Connecting..." : "Connect";
        SendButton.Content = busy && IsConnected ? "Sending..." : "Send";
        UpdateTransportControls();
        DisconnectButton.IsEnabled = IsConnected && !busy;
        SendButton.IsEnabled = IsConnected && !busy;
    }

    private void SetStatus(string message) => StatusTextBlock.Text = message;

    private void AppendOutput(string message)
    {
        if (ResponseTextBox.Text.Length > 0) ResponseTextBox.AppendText(Environment.NewLine + Environment.NewLine);
        ResponseTextBox.AppendText(message);
        ResponseTextBox.ScrollToEnd();
    }

    private void CloseActiveConnection()
    {
        var errors = new List<string>();
        tcpStream?.Dispose();
        tcpStream = null;
        tcpClient?.Dispose();
        tcpClient = null;
        try { activeSerial?.Close(); }
        catch (Exception exception) { errors.Add("Serial close: " + exception.Message); }
        finally { activeSerial = null; }
        try { activeVisa?.Close(); }
        catch (Exception exception) { errors.Add("VISA close: " + exception.Message); }
        finally { activeVisa = null; }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (isBusy)
        {
            e.Cancel = true;
            return;
        }
        try { CloseActiveConnection(); }
        catch (Exception exception) { AppendOutput("CLOSE ERROR: " + exception.Message); }
        base.OnClosing(e);
    }
}
