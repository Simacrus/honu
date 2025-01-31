﻿using System.Collections.Generic;
using watchtower.Models.PSB;

namespace watchtower.Services.Repositories.Readers {

    public class PsbPracticeContactReader : ISheetsReader<PsbPracticeContact> {

        public override PsbPracticeContact ReadEntry(List<string?> values) {
            PsbPracticeContact contact = new();

            contact.Tag = values.GetRequiredString(0);
            contact.Email = values.GetRequiredString(2);
            contact.DiscordID = values.GetNullableUInt64(4) ?? 0;
            contact.Name = values.GetNullableString(1) ?? "<no name given>";

            return contact;
        }

    }
}
