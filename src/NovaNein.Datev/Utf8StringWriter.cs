using System.Text;

namespace NovaNein.Datev;

internal sealed class Utf8StringWriter : StringWriter
{
    public override Encoding Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
