using common;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using RabbitMQ.Client;
using System.Security.Cryptography;
using System.Text;

public class FileController : ControllerBase
{
	private readonly IConnectionFactory _connectionFactory;
	private readonly ILogger<FileController> _logger;
	private readonly FileHashService _fileHashService;

	public FileController(IConnectionFactory connectionFactory, ILogger<FileController> logger, FileHashService fileHashService)
	{
		_connectionFactory = connectionFactory;
		_logger = logger;
		_fileHashService = fileHashService;
	}

	[HttpPost("upload")]
	public async Task<IActionResult> UploadFile(IFormFile file)
	{
		try
		{
			using var stream = new MemoryStream();
			await file.CopyToAsync(stream);
			byte[] fileContent = stream.ToArray();

			string fileHash = ComputeFileHash(fileContent);

			if (!_fileHashService.TryAddHash(fileHash))
			{
				_logger.LogInformation("Файл уже был загружен: {FileName}", file.FileName);
				return BadRequest("Этот файл уже был загружен.");
			}

			string fileExtension = Path.GetExtension(file.FileName);
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file.FileName);

			// Передаем имя файла
			PublishToQueue("sftp_queue", fileContent, fileExtension, fileNameWithoutExtension);

			return Ok("Файл успешно загружен и передан на обработку.");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Ошибка при загрузке файла.");
			return StatusCode(500, "Произошла ошибка при обработке файла.");
		}
	}

	private static string ComputeFileHash(byte[] fileContent)
	{
		using var sha256 = SHA256.Create();
		byte[] hashBytes = sha256.ComputeHash(fileContent);
		return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
	}

	private void PublishToQueue(string queueName, byte[] fileContent, string fileExtension, string fileNameWithoutExtension)
	{
		using var connection = _connectionFactory.CreateConnection();
		using var channel = connection.CreateModel();

		{
			// Убедимся, что очередь существует
			channel.QueueDeclare(queueName, durable: true, exclusive: false, autoDelete: false, arguments: null);

			// Создаем объект с метаинформацией и содержимым файла
			var message = new FileMessage
			{
				FileContent = fileContent,  // Бинарное содержимое файла
				FileExtension = fileExtension,
				FileName = fileNameWithoutExtension // Передаем имя файла
			};

			// Сериализуем объект в JSON
			var jsonMessage = JsonConvert.SerializeObject(message);
			var body = Encoding.UTF8.GetBytes(jsonMessage);

			// Публикуем сообщение в очередь RabbitMQ
			channel.BasicPublish(
				exchange: "",
				routingKey: queueName,
				basicProperties: null,  // без заголовков
				body: body
			);
		}

		_logger.LogInformation("Сообщение добавлено в очередь {QueueName}", queueName);
	}

	// Модель сообщения
	public class FileMessage
	{
		public byte[] FileContent { get; set; } // Бинарное содержимое файла
		public string FileExtension { get; set; } // Расширение файла
		public string FileName { get; set; } // Имя файла
	}
}
