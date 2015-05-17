﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NPoco.Tests.Common
{
    public class CustomerUser
    {
        public int Id { get; set; }
        public string CustomerName { get; set; }
        public string CustomerEmail { get; set; }
    }

    public class CustomerUserJoin
    {
        [Reference]
        public CustomerUser CustomerUser { get; set; }
        public string Name { get; set; }
    }
}
