using System;
using System.Collections.Generic;
using System.Text;

namespace AgileConfig.Client
{
    public interface ILogger
    {
        void LogError(string msg);
        void LogError(Exception ex, string msg);
        void LogTrace(string msg);

    }
}
