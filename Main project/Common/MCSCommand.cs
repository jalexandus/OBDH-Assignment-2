using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OutParsing;

namespace Common
{
    public class MCSCommand
    {
        private string STR = "";    // Unformatted string
        public string[] args;   // Arguments
        public MCSCommand(string str)
        {
            STR = str;    // Unformatted string
            OutParser.Parse(str, "{strings: }", out List<string> strings);
            args = strings.ToArray();
        }
    }
}

    

