using System.Collections.Generic;
using System.Collections.Generic;
using System.Windows;

namespace model_kate.PresentationWpf
{
    public partial class AudioSettingsWindow : Window
    {
        public AudioSettingsWindow()
        {
            InitializeComponent();
            Loaded += AudioSettingsWindow_Loaded;
        }

        private void AudioSettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var inputDevices = new List<DeviceInfo>();
            var outputDevices = new List<DeviceInfo>();
            for (int i = 0; i < NAudio.Wave.WaveIn.DeviceCount; i++)
            {
                var caps = NAudio.Wave.WaveIn.GetCapabilities(i);
                inputDevices.Add(new DeviceInfo
                {
                    index = i,
                    name = caps.ProductName
                });
            }
            for (int i = 0; i < NAudio.Wave.WaveOut.DeviceCount; i++)
            {
                var caps = NAudio.Wave.WaveOut.GetCapabilities(i);
                outputDevices.Add(new DeviceInfo
                {
                    index = i,
                    name = caps.ProductName
                });
            }
            DeviceComboBox.ItemsSource = inputDevices;
            OutputComboBox.ItemsSource = outputDevices;
            if (inputDevices.Count > 0) DeviceComboBox.SelectedIndex = 0;
            if (outputDevices.Count > 0) OutputComboBox.SelectedIndex = 0;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (DeviceComboBox.SelectedItem is DeviceInfo dev && OutputComboBox.SelectedItem is DeviceInfo outDev)
            {
                MessageBox.Show($"Microfone selecionado: {dev.name}\nSaída selecionada: {outDev.name}", "Configuração de Áudio", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
        }

        private class DeviceInfo
        {
            public int index { get; set; }
            public string name { get; set; } = string.Empty;
        }
    }
}
