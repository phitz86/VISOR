using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using VISOR.Telemetry;
using VISOR.ViewModels;

namespace VISOR.Views
{
    public partial class ConfigWindow : Window
    {
        private readonly SVappsLABSDKWrapper _telemetry;

        public ConfigWindow(SVappsLABSDKWrapper telemetry)
        {
            InitializeComponent();
            _telemetry = telemetry;
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            // Set dialog result to true so App.xaml.cs knows we want to launch
            this.DialogResult = true;
            this.Close();
        }

        private void DumpYamlButton_Click(object sender, RoutedEventArgs e) { }
              
    }
}