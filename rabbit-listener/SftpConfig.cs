namespace rabbit_listener
{
	public class SftpConfig
	{
		public string Host { get; set; }
		public int Port { get; set; }
		public string UserName { get; set; }
		public string Password { get; set; }
		public string Source { get; set; }
		public string Destination { get; set; }
	}
}
