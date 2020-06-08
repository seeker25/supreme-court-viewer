﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Scv.Api.Models.Criminal.AppearanceDetail
{
    public class Prosecutor
    {
        public string FullName { get; set; }
        public string PartId { get; set; }
        public string PartyAppearanceMethod { get; set; }
        public string PartyAppearanceMethodDesc { get; set; }
        public string AttendanceMethodCd { get; set; }
        public string AttendanceMethodDesc { get; set; }
    }
}
