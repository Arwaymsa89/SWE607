﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace CSharpLanguage
{

    public class TheExtendedType
    {
        public void Do()
        {

        }
    }
    public static class Extensions
    {
        public static void Slice(this TheExtendedType s, int start, int length)
        {
            s.Do();
        }
    }

}