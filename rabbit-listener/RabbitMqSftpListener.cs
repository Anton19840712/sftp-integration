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
		private readonly ConcurrentQueue<FileMessage> FileQueue = new();

		public RabbitMqSftpListener(
			SftpConfig config,
			IConnectionFactory connectionFactory,
			ILogger<RabbitMqSftpListener> logger)
		{
			_config = config;
			_connectionFactory = connectionFactory;
			_logger = logger;
		}

		public async Task StartAsync(CancellationToken cancellationToken)
		{
			_cts = new CancellationTokenSource();

			_listenerTask = Task.Run(() =>
			{
				_connection = _connectionFactory.CreateConnection();
				_channel = _connection.CreateModel();

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
						FileQueue.Enqueue(message);
						byte[] fileContent = message.FileContent;
						string fileExtension = message.FileExtension;
						string fileName = message.FileName; // Получаем имя файла

						// Сохраняем файл с использованием оригинального имени
						var uniqueName = $"{Path.GetFileNameWithoutExtension(fileName)}{fileExtension}";

						// Тестово для проверки приземляем данные на server
						// Допустим, это будет диск C...
						// var filePath = Path.Combine("C:/pm-storage", uniqueName);
						// await File.WriteAllBytesAsync(filePath, fileContent);

						// Удаляем хэш из обработанных
						string fileHash = ComputeFileHash(fileContent);
						ProcessedFileHashes.TryRemove(fileHash, out _);

						// Подтверждаем сообщение
						_channel.BasicAck(ea.DeliveryTag, false);
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, "Ошибка обработки сообщения.");
						// Здесь можно отправить сообщение в Dead Letter Queue
					}
				};
				_channel.BasicConsume("sftp_queue", false, consumer);}, cancellationToken);

				// Загрузка на SFTP
				await UploadFilesAsync(cancellationToken);
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

			_listenerTask?.Wait();

			_channel?.Close();
			_connection?.Close();

			return Task.CompletedTask;
		}

		private async Task UploadFilesAsync(CancellationToken cancellationToken)
		{
			// Создаем клиент SFTP
			using (var client = new SftpClient(
				_config.Host,
				_config.Port,
				_config.UserName,
				_config.Password))
			{
				try
				{
					await client.ConnectAsync(cancellationToken);
					_logger.LogInformation("Подключение к серверу выполнено для загрузки файлов.");

					// Загружаем файлы из очереди
					while (FileQueue.TryDequeue(out var fileMessage))
					{
						try
						{
							var remoteFilePath = Path.Combine(_config.Source, fileMessage.FileName);

							using (var memoryStream = new MemoryStream(fileMessage.FileContent))
							{
								// Загружаем файл на сервер
								client.UploadFile(memoryStream, remoteFilePath);
								_logger.LogInformation($"Файл {fileMessage.FileName} успешно загружен на сервер в {remoteFilePath}.");
							}
						}
						catch (Exception fileEx)
						{
							_logger.LogError(fileEx, $"Ошибка при загрузке файла {fileMessage.FileName} на SFTP.");
							// Если произошла ошибка, файл остается в очереди для последующих попыток
							FileQueue.Enqueue(fileMessage);
						}
					}
				}
				catch (Exception e)
				{
					_logger.LogError(e, "Ошибка при подключении к SFTP-серверу или загрузке файлов.");
				}
				finally
				{
					if (client.IsConnected)
					{
						client.Disconnect();
						_logger.LogInformation("Отключение от сервера выполнено после завершения загрузки файлов.");
					}
				}
			}
		}
	}
}
