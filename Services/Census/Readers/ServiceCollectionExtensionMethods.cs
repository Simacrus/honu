using System;
using Microsoft.Extensions.DependencyInjection;
using watchtower.Models.Census;
using watchtower.Services.Census.Implementations;

namespace watchtower.Services.Census.Readers {

    public static class ServiceCollectionExtensionMethods {

        public static void AddHonuCensusReadersServices(this IServiceCollection services) {
            services.AddSingleton<ICensusReader<CharacterItem>, CensusCharacterItemReader>();
            services.AddSingleton<ICensusReader<PsCharacterStat>, CensusCharacterStatReader>();
            services.AddSingleton<ICensusReader<OutfitMember>, CensusOutfitMemberReader>();
            services.AddSingleton<ICensusReader<PsDirective>, CensusDirectiveReader>();
            services.AddSingleton<ICensusReader<DirectiveTree>, CensusDirectiveTreeReader>();
            services.AddSingleton<ICensusReader<DirectiveTier>, CensusDirectiveTierReader>();
            services.AddSingleton<ICensusReader<DirectiveTreeCategory>, CensusDirectiveTreeCategoryReader>();
            services.AddSingleton<ICensusReader<CharacterDirective>, CensusCharacterDirectiveReader>();
            services.AddSingleton<ICensusReader<CharacterDirectiveTree>, CensusCharacterDirectiveTreeReader>();
            services.AddSingleton<ICensusReader<CharacterDirectiveTier>, CensusCharacterDirectiveTierReader>();
            services.AddSingleton<ICensusReader<CharacterDirectiveObjective>, CensusCharacterDirectiveObjectiveReader>();
            services.AddSingleton<ICensusReader<ObjectiveType>, CensusObjectiveTypeReader>();
            services.AddSingleton<ICensusReader<ObjectiveSet>, CensusObjectiveSetReader>();
            services.AddSingleton<ICensusReader<PsObjective>, CensusObjectiveReader>();
        }
    }

}