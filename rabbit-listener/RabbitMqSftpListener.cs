using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Renci.SshNet;
using IConnectionFactory = RabbitMQ.Client.IConnectionFactory;

namespace rabbit_listener
{
	public class FileMessage
	{
		public byte[] FileContent { get; set; } // Бинарное содержимое файла
		public string FileExtension { get; set; } // Расширение файла
	}

	public class RabbitMqSftpListener : IHostedService
	{
		private readonly IConnectionFactory _connectionFactory;
		private readonly ILogger<RabbitMqSftpListener> _logger;
		private IConnection _connection;
		private IModel _channel;
		private CancellationTokenSource _cts;
		private Task _listenerTask;
		private readonly SftpConfig _config;
		private static readonly ConcurrentDictionary<string, bool> ProcessedFileHashes = new();
		public RabbitMqSftpListener(
			SftpConfig config,
			IConnectionFactory connectionFactory,
			ILogger<RabbitMqSftpListener> logger)
		{
			_config = config;
			_connectionFactory = connectionFactory;
			_logger = logger;
		}


		public Task StartAsync(CancellationToken cancellationToken)
		{
			_cts = new CancellationTokenSource();

			_listenerTask = Task.Run(() =>
			{
				// Подключаемся к RabbitMQ
				_connection = _connectionFactory.CreateConnection();
				_channel = _connection.CreateModel();

				// Создаем очередь, если она не существует
				_channel.QueueDeclare("sftp_queue", durable: true, exclusive: false, autoDelete: false, arguments: null);

				var consumer = new EventingBasicConsumer(_channel);
				consumer.Received += async (model, ea) =>
				{
					try
					{
						var body = ea.Body.ToArray();
						var jsonMessage = Encoding.UTF8.GetString(body);

						// Десериализуем сообщение
						var message = JsonConvert.DeserializeObject<FileMessage>(jsonMessage);
						byte[] fileContent = message.FileContent;
						string fileExtension = message.FileExtension;

						// Сохраняем файл
						var filePath = Path.Combine("C:/Downloads3", $"file_{Guid.NewGuid()}{fileExtension}");
						await File.WriteAllBytesAsync(filePath, fileContent);

						// Загрузка на SFTP
						 UploadToSftp(filePath);

						// Удаляем хэш из обработанных
						string fileHash = ComputeFileHash(fileContent);
						ProcessedFileHashes.TryRemove(fileHash, out _);

						// Подтверждаем сообщение
						_channel.BasicAck(ea.DeliveryTag, false);
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, "Ошибка обработки сообщения.");
						// Возможно, отправить сообщение в Dead Letter Queue
					}
				};

				_channel.BasicConsume("sftp_queue", false, consumer);

				// Работаем до отмены
				while (!_cts.Token.IsCancellationRequested)
				{
					Task.Delay(1000, _cts.Token).Wait();
				}
			}, cancellationToken);

			return Task.CompletedTask;
		}

		private static string ComputeFileHash(byte[] fileContent)
		{
			using var sha256 = SHA256.Create();
			byte[] hashBytes = sha256.ComputeHash(fileContent);
			return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			_cts.Cancel();

			// Ожидаем завершения задачи
			_listenerTask?.Wait();

			// Закрываем соединение с RabbitMQ
			_channel?.Close();
			_connection?.Close();

			return Task.CompletedTask;
		}

		private void UploadToSftp(string filePath)
		{
			try
			{
				// Здесь можно использовать библиотеку для работы с SFTP, например, SSH.NET
				using (var sftpClient = new SftpClient(
				_config.Host,
				_config.Port,
				_config.UserName,
				_config.Password))
				{
					sftpClient.Connect();
					using (var fileStream = File.OpenRead(filePath))
					{
						var remotePath = Path.Combine("/remote/path", Path.GetFileName(filePath));
						sftpClient.UploadFile(fileStream, remotePath);
					}
					sftpClient.Disconnect();
				}

				_logger.LogInformation("Файл успешно загружен на SFTP сервер.");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка при загрузке файла на SFTP.");
			}
		}
	}
}
