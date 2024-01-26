using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

<<<<<<<< HEAD:src/IO.Swagger.V1RC03/Exceptions/NotImplementedException.cs
namespace IO.Swagger.V1RC03.Exceptions
========
namespace IO.Swagger.Lib.V3.Exceptions
>>>>>>>> upstream/main:src/IO.Swagger.Lib.V3/Exceptions/NotImplementedException.cs
{
    internal class NotImplementedException : Exception
    {
        public NotImplementedException(string message) : base(message)
        {

        }
    }
}
