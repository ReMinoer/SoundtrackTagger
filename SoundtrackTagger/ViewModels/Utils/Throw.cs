using System;
using System.Threading;

namespace SoundtrackTagger.ViewModels.Utils
{
    static public class Throw
    {
        static public void IfTaskCancelled(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
        }
    }
}