using PHax.Core.Interfaces;

namespace PHax.Modem.T30;

/// <summary>Managed T.30 engine over a Class 1 modem.</summary>
public sealed class Class1FaxEngine(IFaxImageEncoder encoder, IFaxImageDecoder decoder, IProtocolLog log, T30Options? options = null)
    : IClass1FaxEngine
{
    public T30Options Options { get; set; } = options ?? new T30Options();

    public IFaxSender CreateSender(IFaxModem modem) => new T30Sender(modem, log, Options, encoder);
    public IFaxReceiver CreateReceiver(IFaxModem modem) => new T30Receiver(modem, log, Options, decoder);
}
