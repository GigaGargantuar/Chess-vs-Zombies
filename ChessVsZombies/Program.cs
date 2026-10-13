namespace ChessVsZombies;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            using var game = new DefenseGame(args);
            game.Run();
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), exception.ToString());
            throw;
        }
    }
}
