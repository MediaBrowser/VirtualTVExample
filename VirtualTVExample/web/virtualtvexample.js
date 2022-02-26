define(['globalize', 'loading', 'appRouter', 'formHelper', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (globalize, loading, appRouter, formHelper) {
    'use strict';

    function onBackClick() {
        appRouter.back();
    }

    function getTunerHostConfiguration(id) {

        if (id) {
            return ApiClient.getTunerHostConfiguration(id);
        } else {
            return ApiClient.getDefaultTunerHostConfiguration('virtualtvexample');
        }
    }

    function reload(view, providerInfo) {

        getTunerHostConfiguration(providerInfo.Id).then(function (info) {

            fillTunerHostInfo(view, info);
        });
    }

    function fillTunerHostInfo(view, info) {

        view.querySelector('.txtFriendlyName').value = info.FriendlyName || '';
    }

    function alertText(options) {
        require(['alert']).then(function (responses) {
            responses[0](options);
        });
    }

    return function (view, params) {

        if (params.id) {
            view.querySelector('.saveButtonText').innerHTML = globalize.translate('Save');
        } else {
            view.querySelector('.saveButtonText').innerHTML = globalize.translate('HeaderAddTvSource');
        }

        view.addEventListener('viewshow', function () {

            reload(view, {
                Id: params.id
            });
        });

        view.querySelector('.btnCancel').addEventListener("click", onBackClick);

        function submitForm(page) {

            loading.show();

            getTunerHostConfiguration(params.id).then(function (info) {

                info.FriendlyName = page.querySelector('.txtFriendlyName').value || null;

                var providerOptions = JSON.parse(info.ProviderOptions || '{}');

                // In reality this would probably come from a picker
                providerOptions.UserId = ApiClient.getCurrentUserId();

                info.ProviderOptions = JSON.stringify(providerOptions);

                ApiClient.saveTunerHostConfiguration(info).then(function (result) {

                    formHelper.handleConfigurationSavedResponse();

                    if (params.id) {
                        appRouter.show(appRouter.getRouteUrl('LiveTVSetup'));
                    } else {
                        appRouter.show(appRouter.getRouteUrl('LiveTVSetup'));
                    }

                }, function () {
                    loading.hide();

                    alertText({
                        text: globalize.translate('ErrorSavingTvProvider')
                    });
                });
            });
        }

        view.querySelector('form').addEventListener('submit', function (e) {
            e.preventDefault();
            e.stopPropagation();
            submitForm(view);
            return false;
        });
    };
});