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
				_logger.LogInformation("���� ��� ��� ��������: {FileName}", file.FileName);
				return BadRequest("���� ���� ��� ��� ��������.");
			}

			string fileExtension = Path.GetExtension(file.FileName);
			PublishToQueue("sftp_queue", fileContent, fileExtension);

			return Ok("���� ������� �������� � ������� �� ���������.");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "������ ��� �������� �����.");
			return StatusCode(500, "��������� ������ ��� ��������� �����.");
		}
	}

	private static string ComputeFileHash(byte[] fileContent)
	{
		using var sha256 = SHA256.Create();
		byte[] hashBytes = sha256.ComputeHash(fileContent);
		return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
	}

	private void PublishToQueue(string queueName, byte[] fileContent, string fileExtension)
	{
		using var connection = _connectionFactory.CreateConnection();
		using var channel = connection.CreateModel();

		{
			// ��������, ��� ������� ����������
			channel.QueueDeclare(queueName, durable: true, exclusive: false, autoDelete: false, arguments: null);

			// ������� ������ � ��������������� � ���������� �����
			var message = new
			{
				FileContent = fileContent,  // �������� ���������� �����
				FileExtension = fileExtension
			};

			// ����������� ������ � JSON
			var jsonMessage = JsonConvert.SerializeObject(message);
			var body = Encoding.UTF8.GetBytes(jsonMessage);

			// ��������� ��������� � ������� RabbitMQ
			channel.BasicPublish(
				exchange: "",
				routingKey: queueName,
				basicProperties: null,  // ��� ����������
				body: body
			);
		}

		_logger.LogInformation("��������� ��������� � ������� {QueueName}", queueName);
	}
}