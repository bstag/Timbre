namespace EposControl.App;

// The same dispatcher guards surround synchronous demo and asynchronous native work.
internal interface IUsbWorkRunner
{
    Task<T> Run<T>(Func<T> work);
}
internal sealed class InlineUsbWorkRunner : IUsbWorkRunner
{
    public Task<T> Run<T>(Func<T> work) => Task.FromResult(work());
}
internal sealed class ThreadedUsbWorkRunner : IUsbWorkRunner
{
    public Task<T> Run<T>(Func<T> work) => Task.Run(work);
}
