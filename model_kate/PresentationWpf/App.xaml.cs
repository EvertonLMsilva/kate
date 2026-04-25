using System;
using System.IO;
using System.Windows;
using model_kate.Infrastructure.Diagnostics;


namespace model_kate.PresentationWpf
{
    public partial class App : System.Windows.Application
	{
		public App()
		{
			this.DispatcherUnhandledException += App_DispatcherUnhandledException;
			AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
			TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
		}

		protected override void OnStartup(StartupEventArgs e)
		{
			base.OnStartup(e);

			try
			{
				var window = new MainWindow
				{
					WindowStartupLocation = WindowStartupLocation.CenterScreen
				};

				MainWindow = window;
				window.Show();
				window.Activate();
				window.Topmost = true;
				window.Topmost = false;
				window.Focus();
				LogFile.AppendLine("[UI] MainWindow exibida no startup.");
			}
			catch (Exception ex)
			{
				try { File.WriteAllText("erro_kate.txt", $"OnStartup: {ex}"); } catch { }
				MessageBox.Show($"Erro ao abrir a janela principal:\n{ex}", "Erro crítico", MessageBoxButton.OK, MessageBoxImage.Error);
				Shutdown();
			}
		}

		private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
		{
			try { File.AppendAllText("erro_kate.txt", $"[{DateTime.Now:HH:mm:ss}] DispatcherUnhandledException: {e.Exception}\n"); } catch { }
			e.Handled = true; // nao fecha o app
		}

		private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
		{
			try { File.AppendAllText("erro_kate.txt", $"[{DateTime.Now:HH:mm:ss}] UnhandledException (isTerminating={e.IsTerminating}): {e.ExceptionObject}\n"); } catch { }
		}

		private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
		{
			e.SetObserved(); // impede que o .NET mate o processo
			try { File.AppendAllText("erro_kate.txt", $"[{DateTime.Now:HH:mm:ss}] UnobservedTaskException: {e.Exception}\n"); } catch { }
		}
	}
}