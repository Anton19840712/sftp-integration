using common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using rabbit_listener;
using RabbitMQ.Client;

public class Program
{
	public static void Main(string[] args)
	{
		CreateHostBuilder(args).Build().Run();
	}

	public static IHostBuilder CreateHostBuilder(string[] args) =>
		Host.CreateDefaultBuilder(args)
			.ConfigureServices((hostContext, services) =>
			{
				var sftpConfig = new SftpConfig
				{
					Host = AppSettings.Host,
					Port = AppSettings.Port,
					UserName = AppSettings.UserName,
					Password = AppSettings.Password,
					Source = AppSettings.Source
				};
				// регистрация сервисов
				services.AddSingleton(sftpConfig); // Регистрируем конфигурацию
												   // Регистрация RabbitMqSftpListener как фонового сервиса
				services.AddSingleton<IConnectionFactory>(sp =>
				{
					var connectionFactory = new ConnectionFactory
					{
						HostName = "localhost",
						UserName = "service",
						Password = "A1qwert"
					};
					return connectionFactory;
				});

				services.AddHostedService<RabbitMqSftpListener>(); // Регистрация как фонового сервиса
				services.AddSingleton<FileHashService>();
			});
}